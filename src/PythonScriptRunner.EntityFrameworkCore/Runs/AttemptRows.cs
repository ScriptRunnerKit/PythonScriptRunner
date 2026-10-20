using Microsoft.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Catalog;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.EntityFrameworkCore.Runs;

/// <summary>Writes the outcome of an attempt (shared by the synchronous recorder and the queued runs).</summary>
internal static class AttemptRows
{
    /// <summary>
    /// Finishes the attempt row and adds its log tails to the context (saved by the caller). With <paramref name="workerId"/>,
    /// only an attempt still running under that worker's lease is finished.
    /// </summary>
    /// <returns>Whether the attempt was finished (<c>false</c>: the lease was lost).</returns>
    public static async Task<bool> FinishAsync(
        PsrDbContext db, DateTimeOffset runCreatedAt, AttemptResult result, DateTimeOffset finishedAt, string? workerId, CancellationToken cancellationToken)
    {
        var error = StoredJson.Error(result.Error);
        var attempts = db.StepAttempts.Where(a => a.Id == result.Id.Value && a.RunCreatedAt == runCreatedAt);
        if (workerId is not null)
        {
            attempts = attempts.Where(a => a.Status == AttemptStatus.Running && a.WorkerId == workerId);
        }

        var finished = await attempts
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, result.Status)
                .SetProperty(a => a.ExitCode, result.ExitCode)
                .SetProperty(a => a.ErrorKind, result.Error == null ? null : result.Error.Kind)
                .SetProperty(a => a.Error, error)
                .SetProperty(a => a.EnvironmentHash, result.EnvironmentHash)
                .SetProperty(a => a.FinishedAt, finishedAt)
                .SetProperty(a => a.DurationMs, (int)result.Duration.TotalMilliseconds)
                .SetProperty(a => a.OverheadMs, (int)result.Overhead.TotalMilliseconds)
                .SetProperty(a => a.LeaseExpiresAt, (DateTimeOffset?)null), cancellationToken).ConfigureAwait(false);
        if (finished == 0)
        {
            return false;
        }

        foreach (var (stream, tail) in (ReadOnlySpan<(LogStream, LogTail)>)[(LogStream.Stdout, result.Stdout), (LogStream.Stderr, result.Stderr)])
        {
            if (tail.OriginalBytes > 0 || tail.Content.Length > 0)
            {
                db.AttemptLogs.Add(new AttemptLogEntity
                {
                    AttemptId = result.Id.Value,
                    RunCreatedAt = runCreatedAt,
                    Stream = stream,
                    Content = tail.Content,
                    Truncated = tail.Truncated,
                    OriginalBytes = tail.OriginalBytes,
                });
            }
        }

        return true;
    }
}
