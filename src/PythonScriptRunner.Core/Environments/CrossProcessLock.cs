using System.Diagnostics;

namespace PythonScriptRunner.Core.Environments;

/// <summary>Exclusive lock shared by every process on the machine (an exclusively opened lock file).</summary>
internal sealed class CrossProcessLock : IDisposable
{
    private readonly FileStream _stream;

    private CrossProcessLock(FileStream stream) => _stream = stream;

    public static async Task<CrossProcessLock> AcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new CrossProcessLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(started) < timeout)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new PythonEnvironmentException($"Timed out waiting for the lock '{path}'.", ex);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
