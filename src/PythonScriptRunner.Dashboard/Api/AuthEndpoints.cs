using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PythonScriptRunner.Dashboard.Access;

namespace PythonScriptRunner.Dashboard.Api;

/// <summary>Access endpoints (D-131): mode of the panel, fixed login and logout, current user.</summary>
internal static partial class AuthEndpoints
{
    /// <summary>Entity type of the login events in the audit.</summary>
    public const string AuditEntityType = "User";

    // Unknown users verify against this hash, so they take as long as wrong passwords (D-135).
    private static readonly Lazy<string> DummyHash = new(() => DashboardPasswords.Hash(Guid.NewGuid().ToString("N")));

    public static void Map(RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Access");
        auth.MapGet("/", GetAuth).AllowAnonymous().WithName("GetAuth").Produces<AuthInfo>();
        auth.MapPost("/login", LoginAsync).AllowAnonymous().WithName("Login")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status429TooManyRequests);
        auth.MapPost("/logout", LogoutAsync).RequireAuthorization(Policies.User).WithName("Logout")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
        api.MapGet("/me", GetMeAsync).RequireAuthorization(Policies.User).WithTags("Access").WithName("GetMe").Produces<MeResponse>();
    }

    /// <summary>The access info of a request (also shown in <c>/admin/settings</c>).</summary>
    public static AuthInfo Info(HttpContext http, DashboardAccessSettings settings, DashboardRoutes routes)
    {
        var returnUrl = Uri.EscapeDataString(http.Request.PathBase.Add(routes.Prefix).Value + "/");
        return new AuthInfo(settings.EffectiveMode, settings.LoginUrl?.Replace("{returnUrl}", returnUrl, StringComparison.Ordinal), settings.LogoutUrl,
            settings.AllowLocalRequests);
    }

    private static AuthInfo GetAuth(HttpContext http, DashboardAccessSettings settings, DashboardRoutes routes) => Info(http, settings, routes);

    private static async Task<IResult> LoginAsync(LoginRequest? request, HttpContext http, DashboardAccessSettings settings, IAuditWriter audit,
        TimeProvider time, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        if (settings.Mode != DashboardAccessMode.FixedLogin)
        {
            return ApiProblems.NotFound(http, "The dashboard does not use the fixed login.");
        }

        var users = http.RequestServices.GetRequiredService<FixedLoginUsers>();
        var throttle = http.RequestServices.GetRequiredService<LoginThrottle>();
        var logger = loggers.CreateLogger(typeof(AuthEndpoints).FullName!);
        var config = users.Settings;
        var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (throttle.CountAttempt(ip, config.MaxAttemptsPerMinutePerIp) is { } wait)
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            LogTooManyAttempts(logger, ip);
            return ApiProblems.Result(http, StatusCodes.Status429TooManyRequests, "TOO_MANY_ATTEMPTS", "Too many login attempts; try again later.");
        }

        var name = request?.Name?.Trim() ?? string.Empty;
        var account = users.Find(name);
        var matches = DashboardPasswords.Verify(account?.PasswordHash ?? DummyHash.Value, request?.Password ?? string.Empty);
        var locked = account is not null && throttle.IsLocked(account.Name);
        if (account is not null && matches && !locked)
        {
            throttle.Succeeded(account.Name);
            await http.SignInAsync(DashboardAccess.SessionScheme, SessionCookieOptions.CreatePrincipal(account),
                SessionCookieOptions.CreateProperties(time.GetUtcNow())).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEntry(Actor.User($"login:{account.Name}"), AuditActions.LoginSucceeded, AuditEntityType, null, account.Name),
                cancellationToken).ConfigureAwait(false);
            return TypedResults.NoContent();
        }

        // Failures go to the log only (D-136); the answer never says whether the user exists or is locked (D-135).
        LogLoginFailed(logger, name.Length <= 100 ? name : name[..100], ip, locked);
        if (account is not null && !locked && throttle.Failed(account.Name, config.MaxFailedAttempts, config.LockoutDuration))
        {
            LogUserLocked(logger, account.Name, config.LockoutDuration);
            await audit.WriteAsync(new AuditEntry(Actor.User($"login:{account.Name}"), AuditActions.UserLocked, AuditEntityType, null, account.Name),
                cancellationToken).ConfigureAwait(false);
        }

        return ApiProblems.Result(http, StatusCodes.Status401Unauthorized, "LOGIN_FAILED", "Invalid user name or password.");
    }

    private static async Task<IResult> LogoutAsync(HttpContext http, DashboardAccessSettings settings, DashboardAccess access, IAuditWriter audit,
        CancellationToken cancellationToken)
    {
        if (settings.Mode != DashboardAccessMode.FixedLogin)
        {
            return ApiProblems.NotFound(http, "The dashboard does not use the fixed login.");
        }

        var user = await access.ResolveAsync(http).ConfigureAwait(false);
        await http.SignOutAsync(DashboardAccess.SessionScheme).ConfigureAwait(false);
        if (user is { Mode: DashboardAccessMode.FixedLogin })
        {
            await audit.WriteAsync(new AuditEntry(user.Actor, AuditActions.LoggedOut, AuditEntityType, null, user.Name), cancellationToken).ConfigureAwait(false);
        }

        return TypedResults.NoContent();
    }

    private static async Task<MeResponse> GetMeAsync(HttpContext http, DashboardAccess access)
    {
        var user = (await access.ResolveAsync(http).ConfigureAwait(false))!;
        return new MeResponse(user.Subject, user.Name, user.Role, user.Roles, user.Mode);
    }

    [LoggerMessage(310, LogLevel.Warning, "Dashboard login failed for '{Name}' from {Ip} (locked: {Locked})")]
    private static partial void LogLoginFailed(ILogger logger, string name, string ip, bool locked);

    [LoggerMessage(311, LogLevel.Warning, "Dashboard user '{Name}' locked for {Duration} after consecutive login failures")]
    private static partial void LogUserLocked(ILogger logger, string name, TimeSpan duration);

    [LoggerMessage(312, LogLevel.Warning, "Too many dashboard login attempts from {Ip}")]
    private static partial void LogTooManyAttempts(ILogger logger, string ip);
}

/// <summary>Authorization policies of the dashboard (roles are cumulative; D-28).</summary>
internal static class Policies
{
    public const string User = DashboardRoleRequirement.UserPolicy;
    public const string Reader = "psr:reader";
    public const string Executor = "psr:executor";
    public const string Author = "psr:author";
    public const string Admin = "psr:admin";
}
