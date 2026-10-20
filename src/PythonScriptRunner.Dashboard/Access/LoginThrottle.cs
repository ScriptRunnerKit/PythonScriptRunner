using System.Collections.Concurrent;

namespace PythonScriptRunner.Dashboard.Access;

/// <summary>
/// Brute force protection of the fixed login (D-126, D-135): consecutive failures lock a user for a while, and attempts per
/// IP are limited per minute. Counters live in memory, per instance.
/// </summary>
internal sealed class LoginThrottle(TimeProvider time)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, UserState> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IpState> _ips = new(StringComparer.Ordinal);

    /// <summary>Counts an attempt of an IP; returns how long to wait when over the limit.</summary>
    public TimeSpan? CountAttempt(string ip, int maxPerMinute)
    {
        var now = time.GetUtcNow();
        if (_ips.Count > 10_000)
        {
            // Keeps memory bounded when many addresses try.
            foreach (var (address, old) in _ips)
            {
                if (old.Attempts.Count == 0 || now - old.Attempts.Last() >= Window)
                {
                    _ips.TryRemove(address, out _);
                }
            }
        }

        var state = _ips.GetOrAdd(ip, _ => new IpState());
        lock (state)
        {
            while (state.Attempts.Count > 0 && now - state.Attempts.Peek() >= Window)
            {
                state.Attempts.Dequeue();
            }

            if (state.Attempts.Count >= maxPerMinute)
            {
                return state.Attempts.Peek() + Window - now;
            }

            state.Attempts.Enqueue(now);
            return null;
        }
    }

    /// <summary>Whether the user is locked now.</summary>
    public bool IsLocked(string user) =>
        _users.TryGetValue(user, out var state) && state.LockedUntil is { } until && until > time.GetUtcNow();

    /// <summary>Records a failure; returns <c>true</c> when it locks the user.</summary>
    public bool Failed(string user, int maxFailures, TimeSpan lockout)
    {
        var state = _users.GetOrAdd(user, _ => new UserState());
        lock (state)
        {
            state.Failures++;
            if (state.Failures < maxFailures)
            {
                return false;
            }

            state.Failures = 0;
            state.LockedUntil = time.GetUtcNow() + lockout;
            return true;
        }
    }

    /// <summary>A successful login clears the failures of the user.</summary>
    public void Succeeded(string user) => _users.TryRemove(user, out _);

    private sealed class UserState
    {
        public int Failures { get; set; }

        public DateTimeOffset? LockedUntil { get; set; }
    }

    private sealed class IpState
    {
        public Queue<DateTimeOffset> Attempts { get; } = new();
    }
}
