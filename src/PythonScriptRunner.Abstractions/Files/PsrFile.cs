using System.Security.Cryptography;
using System.Text;

namespace PythonScriptRunner;

/// <summary>A file sent to or received from a stage.</summary>
public sealed class PsrFile
{
    private static readonly Dictionary<string, string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".zip"] = "application/zip",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    };

    private readonly string? _path;
    private readonly ReadOnlyMemory<byte>? _bytes;
    private Stream? _stream;
    private string? _sha256;

    private PsrFile(string fileName, string contentType, string? path, ReadOnlyMemory<byte>? bytes, Stream? stream, string? sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        FileName = Path.GetFileName(fileName);
        ContentType = contentType;
        _path = path;
        _bytes = bytes;
        _stream = stream;
        _sha256 = sha256;
    }

    /// <summary>A file on disk.</summary>
    /// <param name="path">File path.</param>
    /// <param name="contentType">MIME type; guessed from the extension when omitted.</param>
    /// <param name="fileName">Name presented to the script; defaults to the file name of <paramref name="path"/>.</param>
    public static PsrFile FromPath(string path, string? contentType = null, string? fileName = null)
    {
        var fullPath = Path.GetFullPath(path);
        if (!System.IO.File.Exists(fullPath))
        {
            throw new FileNotFoundException("File not found.", fullPath);
        }

        var name = fileName ?? Path.GetFileName(fullPath);
        return new PsrFile(name, contentType ?? GuessContentType(name), fullPath, null, null, null);
    }

    /// <summary>A file read once from a stream (the stream is consumed when the run starts).</summary>
    /// <param name="stream">Readable stream.</param>
    /// <param name="fileName">File name presented to the script.</param>
    /// <param name="contentType">MIME type.</param>
    public static PsrFile FromStream(Stream stream, string fileName, string contentType)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new PsrFile(fileName, contentType, null, null, stream, null);
    }

    /// <summary>A file held in memory.</summary>
    /// <param name="bytes">Content.</param>
    /// <param name="fileName">File name presented to the script.</param>
    /// <param name="contentType">MIME type.</param>
    public static PsrFile FromBytes(ReadOnlyMemory<byte> bytes, string fileName, string contentType) =>
        new(fileName, contentType, null, bytes, null, null);

    /// <summary>A text file encoded as UTF-8.</summary>
    /// <param name="text">Content.</param>
    /// <param name="fileName">File name presented to the script.</param>
    /// <param name="contentType">MIME type. Default: <c>text/plain</c>.</param>
    public static PsrFile FromText(string text, string fileName, string contentType = "text/plain") =>
        FromBytes(Encoding.UTF8.GetBytes(text), fileName, contentType);

    /// <summary>A file produced by a stage (path, size and hash already known).</summary>
    /// <param name="path">File path.</param>
    /// <param name="fileName">File name.</param>
    /// <param name="contentType">MIME type.</param>
    /// <param name="sha256">SHA-256 as lowercase hex.</param>
    public static PsrFile FromProducedFile(string path, string fileName, string contentType, string sha256) =>
        new(fileName, contentType, Path.GetFullPath(path), null, null, sha256);

    /// <summary>File name (without directories).</summary>
    public string FileName { get; }

    /// <summary>MIME type.</summary>
    public string ContentType { get; }

    /// <summary>Path on disk, when the file is backed by one.</summary>
    public string? LocalPath => _path;

    /// <summary>Size in bytes; <c>null</c> for a non-seekable stream that was not read yet.</summary>
    public long? Length
    {
        get
        {
            if (_path is not null)
            {
                return new FileInfo(_path).Length;
            }

            if (_bytes is { } bytes)
            {
                return bytes.Length;
            }

            return _stream is { CanSeek: true } stream ? stream.Length - stream.Position : null;
        }
    }

    /// <summary>SHA-256 as lowercase hex; <c>null</c> for a stream that was not read yet.</summary>
    public string? Sha256
    {
        get
        {
            if (_sha256 is not null)
            {
                return _sha256;
            }

            if (_path is not null)
            {
                using var file = System.IO.File.OpenRead(_path);
                _sha256 = Convert.ToHexStringLower(SHA256.HashData(file));
            }
            else if (_bytes is { } bytes)
            {
                _sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
            }

            return _sha256;
        }
    }

    /// <summary>Opens the content for reading.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">A stream-backed file was already read.</exception>
    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_path is not null)
        {
            Stream file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return Task.FromResult(file);
        }

        if (_bytes is { } bytes)
        {
            return Task.FromResult<Stream>(new MemoryStream(bytes.ToArray(), writable: false));
        }

        var stream = Interlocked.Exchange(ref _stream, null)
            ?? throw new InvalidOperationException($"The stream of file '{FileName}' was already consumed.");
        return Task.FromResult(stream);
    }

    /// <summary>Copies the content to a file.</summary>
    /// <param name="path">Destination path (overwritten).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SaveAsAsync(string path, CancellationToken cancellationToken = default)
    {
        var source = await OpenReadAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (target.ConfigureAwait(false))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>MIME type guessed from the file extension (<c>application/octet-stream</c> when unknown).</summary>
    /// <param name="fileName">File name.</param>
    public static string GuessContentType(string fileName) =>
        KnownTypes.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";

    /// <inheritdoc />
    public override string ToString() => $"{FileName} ({ContentType})";
}
