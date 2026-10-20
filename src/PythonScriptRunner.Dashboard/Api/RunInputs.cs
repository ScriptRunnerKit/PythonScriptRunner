using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace PythonScriptRunner.Dashboard.Api;

/// <summary>
/// Inputs of a run or test sent by the panel: <c>multipart/form-data</c> with an <c>inputs</c> JSON part, one part per file
/// (named after its field) and optional <c>version</c> / <c>idempotencyKey</c> parts — or a JSON <see cref="RunRequest"/> when
/// there are no files. Files are streamed to temporary files (≤ 50 MB each, ≤ 200 MB per request; 413 otherwise) and deleted
/// when this is disposed, after the run stored them.
/// </summary>
internal sealed class RunInputs : IAsyncDisposable
{
    private const int MaxTextPartBytes = 10 * 1024 * 1024;

    private readonly List<string> _temporaryFiles = [];
    private readonly Dictionary<string, List<PsrFile>> _files = new(StringComparer.Ordinal);
    private JsonObject _values = [];

    private RunInputs()
    {
    }

    /// <summary>Version to run, when given.</summary>
    public int? Version { get; private set; }

    /// <summary>Idempotency key, when given.</summary>
    public string? IdempotencyKey { get; private set; }

    /// <summary>Reads the request. Throws <see cref="PayloadTooLargeException"/> above the limits and <see cref="ArgumentException"/> for malformed parts.</summary>
    public static async Task<RunInputs> ReadAsync(HttpContext http, PythonScriptRunnerOptions limits, CancellationToken cancellationToken)
    {
        var inputs = new RunInputs();
        try
        {
            var request = http.Request;
            if (request.HasFormContentType && MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType)
                && HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value is { Length: > 0 } boundary)
            {
                if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                {
                    // Room for the files plus the text parts and multipart headers.
                    bodySize.MaxRequestBodySize = limits.MaxRunFilesBytes + MaxTextPartBytes;
                }

                await inputs.ReadMultipartAsync(new MultipartReader(boundary, request.Body), limits, Path.Combine(limits.WorkRoot, "uploads"), cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
            {
                var body = await request.ReadFromJsonAsync<RunRequest>(cancellationToken).ConfigureAwait(false);
                inputs._values = body?.Inputs ?? [];
                inputs.Version = body?.Version;
                inputs.IdempotencyKey = body?.IdempotencyKey;
            }

            return inputs;
        }
        catch
        {
            await inputs.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The inputs for <c>IRunQueue</c> / <c>ICatalogAdmin</c>: JSON values plus the files of each file field.</summary>
    /// <param name="fields">Declared inputs (to tell a single file from a list); <c>null</c> when unknown.</param>
    public Dictionary<string, object?> Values(IReadOnlyList<FieldDefinition>? fields)
    {
        var values = _values.ToDictionary(p => p.Key, p => (object?)p.Value?.DeepClone(), StringComparer.Ordinal);
        foreach (var (name, files) in _files)
        {
            var isList = fields?.FirstOrDefault(f => f.Name == name)?.Type.Kind == FieldKind.List;
            values[name] = isList || files.Count > 1 ? files : files[0];
        }

        return values;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var file in _temporaryFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // Best effort: the work root janitor removes leftovers.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort.
            }
        }

        return ValueTask.CompletedTask;
    }

    private async Task ReadMultipartAsync(MultipartReader reader, PythonScriptRunnerOptions limits, string folder, CancellationToken cancellationToken)
    {
        long total = 0;
        while (await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || HeaderUtilities.RemoveQuotes(disposition.Name).Value is not { Length: > 0 } name)
            {
                throw new ArgumentException("Every multipart part needs a Content-Disposition with a name.");
            }

            var fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
            if (fileName is null)
            {
                await ReadTextPartAsync(name, section, cancellationToken).ConfigureAwait(false);
                continue;
            }

            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N"));
            _temporaryFiles.Add(path);
            long size = 0;
            var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (target.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await section.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    size += read;
                    total += read;
                    if (size > limits.MaxFileBytes)
                    {
                        throw new PayloadTooLargeException($"The file '{Path.GetFileName(fileName)}' of '{name}' exceeds {limits.MaxFileBytes / (1024 * 1024)} MB.");
                    }

                    if (total > limits.MaxRunFilesBytes)
                    {
                        throw new PayloadTooLargeException($"The files of the request exceed {limits.MaxRunFilesBytes / (1024 * 1024)} MB.");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            var safeName = Path.GetFileName(fileName.Replace('\\', '/'));
            // Only the media type counts (browsers may add parameters such as charset to text files).
            var contentType = MediaTypeHeaderValue.TryParse(section.ContentType, out var media) && media.MediaType.HasValue
                ? media.MediaType.Value!
                : PsrFile.GuessContentType(safeName);
            if (!_files.TryGetValue(name, out var list))
            {
                _files[name] = list = [];
            }

            list.Add(PsrFile.FromPath(path, contentType, safeName.Length > 0 ? safeName : "file"));
        }
    }

    private async Task ReadTextPartAsync(string name, MultipartSection section, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await section.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxTextPartBytes)
            {
                throw new PayloadTooLargeException($"The part '{name}' exceeds {MaxTextPartBytes / (1024 * 1024)} MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        var text = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        switch (name)
        {
            case "inputs":
                _values = text.Length == 0
                    ? []
                    : JsonNode.Parse(text) as JsonObject ?? throw new ArgumentException("The 'inputs' part must be a JSON object.");
                break;
            case "version":
                Version = text.Length == 0 ? null
                    : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version
                    : throw new ArgumentException("The 'version' part must be an integer.");
                break;
            case "idempotencyKey":
                IdempotencyKey = text.Length == 0 ? null : text;
                break;
            default:
                throw new ArgumentException($"Unexpected text part '{name}': inputs go in the 'inputs' JSON part and files in parts named after their field.");
        }
    }
}

/// <summary>Helpers shared by the endpoints.</summary>
internal static class Endpoints
{
    /// <summary>The user of an authorized request, as the author of changes.</summary>
    public static async ValueTask<Actor> ActorAsync(this HttpContext http) =>
        (await http.RequestServices.GetRequiredService<Access.DashboardAccess>().ResolveAsync(http).ConfigureAwait(false))?.Actor
        ?? throw new InvalidOperationException("The dashboard user is resolved by the authorization of the endpoint.");

    /// <summary>Location of a run in the API.</summary>
    public static string RunLocation(this HttpContext http, RunId runId) =>
        $"{http.Request.PathBase}{http.RequestServices.GetRequiredService<Access.DashboardRoutes>().Prefix}/api/runs/{runId}";

    /// <summary>Writes a zip produced by an export.</summary>
    public static async Task<IResult> ZipAsync(Func<Stream, Task> export, string fileName)
    {
        var zip = new MemoryStream();
        await export(zip).ConfigureAwait(false);
        zip.Position = 0;
        return TypedResults.File(zip, "application/zip", fileName);
    }
}
