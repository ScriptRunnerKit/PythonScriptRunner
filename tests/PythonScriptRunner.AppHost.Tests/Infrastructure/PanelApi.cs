using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace PythonScriptRunner.Tests.Infrastructure;

/// <summary>
/// The dashboard API as an author uses it (spec 007): fixed login, import of a test fixture, publication and runs. Shared
/// by the Aspire AppHost tests and the docker-compose test (spec 009).
/// </summary>
public static class PanelApi
{
    public const string DevPassword = "psr-dev";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A client of the dashboard at <paramref name="baseAddress"/>, signed in with the fixed login.</summary>
    public static async Task<HttpClient> LoginAsync(Uri baseAddress, string name = "autor", string password = DevPassword)
    {
        var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = baseAddress };
        http.DefaultRequestHeaders.Add("X-PSR-Request", "1");
        var login = await http.PostAsJsonAsync("/psr/api/auth/login", new { name, password }, Ct);
        login.StatusCode.ShouldBe(HttpStatusCode.NoContent, await login.Content.ReadAsStringAsync(Ct));
        return http;
    }

    /// <summary>Imports a fixture of <c>tests/fixtures</c> as a zip (drafts), then publishes its stages and pipelines.</summary>
    public static async Task ImportAndPublishAsync(HttpClient http, string fixture, Func<string, string, string>? transform = null)
    {
        var root = Path.Combine(RepositoryRoot(), "tests", "fixtures", fixture);
        var files = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => !f.Contains("__pycache__", StringComparison.Ordinal)))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var content = await File.ReadAllTextAsync(file, Ct);
            files[relative] = transform is null ? content : transform(relative, content);
        }

        await ImportAndPublishAsync(http, fixture, files);
    }

    /// <summary>Imports files in the folder format (<c>stages/&lt;key&gt;/…</c>, <c>pipelines/…</c>) and publishes them.</summary>
    public static async Task ImportAndPublishAsync(HttpClient http, string name, IReadOnlyDictionary<string, string> files)
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                await using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                await writer.WriteAsync(content);
            }
        }

        using var form = new MultipartFormDataContent();
        var archiveContent = new ByteArrayContent(zip.ToArray());
        archiveContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(archiveContent, "archive", $"{name}.zip");
        var import = await http.PostAsync("/psr/api/import", form, Ct);
        import.StatusCode.ShouldBe(HttpStatusCode.OK, await import.Content.ReadAsStringAsync(Ct));
        var items = (await import.Content.ReadFromJsonAsync<JsonObject>(Ct))!["items"]!.AsArray();

        // Stages first: the pipeline references their published versions.
        foreach (var item in items.OrderBy(i => i!["kind"]!.GetValue<string>() == "Stage" ? 0 : 1))
        {
            var kind = item!["kind"]!.GetValue<string>() == "Stage" ? "stages" : "pipelines";
            var key = item["key"]!.GetValue<string>();
            var draft = await http.GetAsync($"/psr/api/{kind}/{key}/draft", Ct);
            using var publish = new HttpRequestMessage(HttpMethod.Post, $"/psr/api/{kind}/{key}/draft/publish");
            publish.Headers.IfMatch.Add(draft.Headers.ETag!);
            var published = await http.SendAsync(publish, Ct);
            published.StatusCode.ShouldBe(HttpStatusCode.OK, await published.Content.ReadAsStringAsync(Ct));
        }
    }

    /// <summary>Starts a pipeline run (JSON or multipart body) and returns its id.</summary>
    public static Task<string> RunAsync(HttpClient http, string pipeline, HttpContent body) => StartAsync(http, $"pipelines/{pipeline}", body);

    /// <summary>Starts a run of a single stage and returns its id.</summary>
    public static Task<string> RunStageAsync(HttpClient http, string stage, HttpContent body) => StartAsync(http, $"stages/{stage}", body);

    /// <summary>Waits until the run ends (up to <paramref name="timeout"/>, default 5 min) and returns it, whatever the status.</summary>
    public static async Task<JsonObject> WaitForEndAsync(HttpClient http, string runId, TimeSpan? timeout = null)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        while (true)
        {
            var run = (await http.GetFromJsonAsync<JsonObject>($"/psr/api/runs/{runId}", limit.Token))!;
            if (run["status"]!.GetValue<string>() is "Succeeded" or "Failed" or "Cancelled" or "TimedOut")
            {
                return run;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), limit.Token);
        }
    }

    private static async Task<string> StartAsync(HttpClient http, string target, HttpContent body)
    {
        var response = await http.PostAsync($"/psr/api/{target}/runs", body, Ct);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!["runId"]!.GetValue<string>();
    }

    /// <summary>Waits until the run ends (up to <paramref name="timeout"/>, default 5 min) and checks it succeeded.</summary>
    public static async Task<JsonObject> WaitForSuccessAsync(HttpClient http, string runId, TimeSpan? timeout = null)
    {
        var run = await WaitForEndAsync(http, runId, timeout);
        run["status"]!.GetValue<string>().ShouldBe("Succeeded", run["error"]?.ToJsonString());
        return run;
    }

    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PythonScriptRunner.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("The repository root (PythonScriptRunner.slnx) was not found.");
    }
}
