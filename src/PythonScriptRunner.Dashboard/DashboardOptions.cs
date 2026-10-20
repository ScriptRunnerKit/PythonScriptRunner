using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;

namespace PythonScriptRunner.Dashboard;

/// <summary>Cumulative roles of the dashboard (D-28): each one can do everything the previous ones can.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DashboardRole>))]
public enum DashboardRole
{
    /// <summary>Sees the catalog, runs, logs, files and schedules.</summary>
    Reader,

    /// <summary>+ starts, cancels and reprocesses runs.</summary>
    Executor,

    /// <summary>+ edits, tests, publishes and archives stages and pipelines, imports, schedules.</summary>
    Author,

    /// <summary>+ deletes, shortens retention, sees secret status and the audit.</summary>
    Admin,
}

/// <summary>How the dashboard identifies its users (ADR-0021).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DashboardAccessMode>))]
public enum DashboardAccessMode
{
    /// <summary>Users configured for the dashboard, with its own login and session cookie.</summary>
    FixedLogin,

    /// <summary>An <see cref="IDashboardAuthorizationFilter"/> decides each request.</summary>
    Filter,

    /// <summary>The host authenticates; its policies give the roles.</summary>
    Host,

    /// <summary>Only local requests (Development), as Admin.</summary>
    Localhost,
}

/// <summary>A user allowed by a custom filter.</summary>
/// <param name="Name">Name shown in the dashboard and recorded in the audit.</param>
/// <param name="Role">Role of the user.</param>
public sealed record DashboardUser(string Name, DashboardRole Role);

/// <summary>Decides who may use the dashboard, like Hangfire's filters (D-123).</summary>
public interface IDashboardAuthorizationFilter
{
    /// <summary>Identifies the user of a request.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The user and role, or <c>null</c> to deny (401).</returns>
    ValueTask<DashboardUser?> AuthorizeAsync(HttpContext context);
}

/// <summary>Settings of the dashboard (spec 007). Choose exactly one main access mode, and optionally local requests in Development.</summary>
public sealed class DashboardOptions
{
    internal DashboardAccessMode? Mode { get; private set; }

    internal FixedLoginOptions FixedLogin { get; } = new();

    internal HostAuthenticationOptions HostAuthentication { get; } = new();

    internal Type? FilterType { get; private set; }

    internal int MainModes { get; private set; }

    /// <summary>
    /// Lets local requests in as Admin without login (D-134). Allowed only in Development: elsewhere startup fails. Alone, it is
    /// the access mode; with a main mode, local requests skip it.
    /// </summary>
    public bool AllowLocalRequests { get; set; }

    /// <summary>Fixed login: users and roles from configuration or code, login screen of the panel, session cookie (D-119..D-121).</summary>
    /// <param name="configure">Users, session and lockout settings.</param>
    public DashboardOptions UseFixedLogin(Action<FixedLoginOptions>? configure = null)
    {
        SetMode(DashboardAccessMode.FixedLogin);
        configure?.Invoke(FixedLogin);
        return this;
    }

    /// <summary>Custom filter (resolved from DI, registered as scoped when missing) decides each request (D-123).</summary>
    /// <typeparam name="TFilter">Filter type.</typeparam>
    public DashboardOptions UseAuthorizationFilter<TFilter>()
        where TFilter : class, IDashboardAuthorizationFilter
    {
        SetMode(DashboardAccessMode.Filter);
        FilterType = typeof(TFilter);
        return this;
    }

    /// <summary>The host authenticates (cookie, OIDC, JWT...) and its policies give the four roles.</summary>
    /// <param name="configure">Policies of the roles and the host login/logout URLs.</param>
    public DashboardOptions UseHostAuthentication(Action<HostAuthenticationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        SetMode(DashboardAccessMode.Host);
        configure(HostAuthentication);
        return this;
    }

    private void SetMode(DashboardAccessMode mode)
    {
        Mode = mode;
        MainModes++;
    }
}

/// <summary>Fixed login settings.</summary>
public sealed class FixedLoginOptions
{
    /// <summary>Default configuration section of the users.</summary>
    public const string DefaultSection = "PythonScriptRunner:Dashboard:Login";

    internal string? ConfigurationSection { get; private set; }

    /// <summary>Users defined in code (ignored when <see cref="BindConfiguration"/> is used).</summary>
    public IList<FixedLoginUser> Users { get; } = [];

    /// <summary>Session ends after this time without use. Default: 8 hours (D-127).</summary>
    public TimeSpan SlidingExpiration { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Session ends this long after the login, even when used. Default: 24 hours (D-127).</summary>
    public TimeSpan AbsoluteExpiration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Consecutive failures that lock a user. Default: 5 (D-126).</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>How long a user stays locked. Default: 15 minutes (D-126).</summary>
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Login attempts per minute from one IP before 429. Default: 10 (D-126).</summary>
    public int MaxAttemptsPerMinutePerIp { get; set; } = 10;

    /// <summary>Keeps the keys that protect the session cookie in <c>psr.dashboard_keys</c> (D-132). <c>false</c> uses the host's Data Protection.</summary>
    public bool PersistKeysInDatabase { get; set; } = true;

    /// <summary>Reads the users (and these settings) from a configuration section, reloaded when it changes (D-128).</summary>
    /// <param name="section">Section path. Default: <see cref="DefaultSection"/>.</param>
    public FixedLoginOptions BindConfiguration(string section = DefaultSection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section);
        ConfigurationSection = section;
        return this;
    }
}

/// <summary>A user of the fixed login.</summary>
public sealed class FixedLoginUser
{
    /// <summary>User name (1-100 chars, compared ignoring case).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Role.</summary>
    public DashboardRole Role { get; set; }

    /// <summary>Password hash generated by <c>psr hash-password</c> or <see cref="DashboardPasswords.Hash"/>; never the password.</summary>
    public string PasswordHash { get; set; } = string.Empty;
}

/// <summary>Host authentication settings: one policy per role and the host login/logout pages.</summary>
public sealed class HostAuthenticationOptions
{
    /// <summary>Policy of the Reader role (e.g. <c>p =&gt; p.RequireRole("psr-leitor")</c>).</summary>
    public Action<AuthorizationPolicyBuilder>? Reader { get; set; }

    /// <summary>Policy of the Executor role.</summary>
    public Action<AuthorizationPolicyBuilder>? Executor { get; set; }

    /// <summary>Policy of the Author role.</summary>
    public Action<AuthorizationPolicyBuilder>? Author { get; set; }

    /// <summary>Policy of the Admin role.</summary>
    public Action<AuthorizationPolicyBuilder>? Admin { get; set; }

    /// <summary>Login page of the host; <c>{returnUrl}</c> is replaced by the panel URL.</summary>
    public string? LoginUrl { get; set; }

    /// <summary>Logout page of the host.</summary>
    public string? LogoutUrl { get; set; }

    /// <summary>Authentication schemes used to read the user (default: the host's default scheme).</summary>
    public IList<string> AuthenticationSchemes { get; } = [];
}

/// <summary>Password hashes of the fixed login (PBKDF2 in the format of ASP.NET Identity v3; D-121).</summary>
public static class DashboardPasswords
{
    private static readonly PasswordHasher<object> Hasher = new();

    /// <summary>Hashes a password for <see cref="FixedLoginUser.PasswordHash"/>.</summary>
    /// <param name="password">The password.</param>
    public static string Hash(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return Hasher.HashPassword(null!, password);
    }

    /// <summary>Whether <paramref name="password"/> matches <paramref name="hash"/>.</summary>
    /// <param name="hash">Stored hash.</param>
    /// <param name="password">Typed password.</param>
    public static bool Verify(string hash, string password) =>
        Hasher.VerifyHashedPassword(null!, hash, password) != PasswordVerificationResult.Failed;

    /// <summary>Whether <paramref name="hash"/> is a hash of this format (Identity v3), not a plain password.</summary>
    /// <param name="hash">The value.</param>
    public static bool IsHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(hash);
            return bytes.Length > 13 && bytes[0] == 0x01;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
