using System.Text;

namespace PythonScriptRunner.Core.Execution;

/// <summary>Keeps the last <c>capacity</c> bytes written to a stream.</summary>
internal sealed class TailBuffer(int capacity)
{
    private readonly byte[] _buffer = new byte[capacity];
    private int _start;
    private int _count;
    private long _total;

    public long TotalBytes => _total;

    public void Append(ReadOnlySpan<byte> data)
    {
        _total += data.Length;
        if (data.Length >= capacity)
        {
            data[^capacity..].CopyTo(_buffer);
            _start = 0;
            _count = capacity;
            return;
        }

        var end = (_start + _count) % capacity;
        var firstPart = Math.Min(data.Length, capacity - end);
        data[..firstPart].CopyTo(_buffer.AsSpan(end));
        data[firstPart..].CopyTo(_buffer);

        var overflow = _count + data.Length - capacity;
        if (overflow > 0)
        {
            _start = (_start + overflow) % capacity;
            _count = capacity;
        }
        else
        {
            _count += data.Length;
        }
    }

    public LogTail ToLogTail(SecretMasker masker)
    {
        var bytes = new byte[_count];
        var firstPart = Math.Min(_count, capacity - _start);
        _buffer.AsSpan(_start, firstPart).CopyTo(bytes);
        _buffer.AsSpan(0, _count - firstPart).CopyTo(bytes.AsSpan(firstPart));
        return new LogTail(masker.Mask(Encoding.UTF8.GetString(bytes)), _total > _count, _total);
    }

    public async Task PumpAsync(Stream source, CancellationToken cancellationToken)
    {
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            Append(chunk.AsSpan(0, read));
        }
    }
}

/// <summary>Replaces secret values with <c>***</c> in captured output.</summary>
internal sealed class SecretMasker(IEnumerable<string> secrets)
{
    private const int MinimumLength = 3;

    private readonly string[] _values = [.. secrets
        .Where(s => s.Length >= MinimumLength)
        .Distinct(StringComparer.Ordinal)
        .OrderByDescending(s => s.Length)];

    public static SecretMasker None { get; } = new([]);

    public string Mask(string text)
    {
        foreach (var value in _values)
        {
            text = text.Replace(value, "***", StringComparison.Ordinal);
        }

        return text;
    }
}
