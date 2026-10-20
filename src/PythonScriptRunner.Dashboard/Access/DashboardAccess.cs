using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace PythonScriptRunner.Dashboard.Access;

/// <summary>The user of a dashboard request.</summary>
/// <param name="Subject">Recorded in the audit and on the runs: <c>login:&lt;name&gt;</c>, <c>filter:&lt;name&gt;</c>, <c>local</c> or the host <c>sub</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="Role">Role, or <c>null</c> when authenticated by the host without any dashboard role (403).</param>
/// <param name="Mode">How the user was identified.</param>
internal sealed record DashboardPrincipal(string Subject, string Name, DashboardRole? Role, DashboardAccessMode Mode)
{
    /// <summary>The user as the author of changes (the subject fits the 200 chars of the audit and authorship columns).</summary>
    public Actor Actor => Actor.User(Subject.Length <= 200 ? Subject : Subject[..200]);

    /// <summary>The roles the user has (cumulative).</summary>
    public IReadOnlyList<DashboardRole> Roles => Role is { } role ? [.. Enum.GetValues<DashboardRole>().Where(r => r <= role)] : [];
}

/// <summary>Resolved access settings (validated by <c>AddDashboard</c>).</summary>
/// <param name="Mode">Main mode, or <c>null</c> when only local requests are allowed.</param>
/// <param name="AllowLocalRequests">Local requests enter as Admin.</param>
/// <param name="FilterType">Filter of the filter mode.</param>
/// <param name="RolePolicies">Policies of the roles in the host mode.</param>
/// <param name="HostSchemes">Schemes that read the host user.</param>
/// <param name="LoginUrl">Host login page.</param>
/// <param name="LogoutUrl">Host logout page.</param>
internal sealed record DashboardAccessSettings(
    DashboardAccessMode? Mode,
    bool AllowLocalRequests,
    Type? FilterType,
    IReadOnlyDictionary<DashboardRole, AuthorizationPolicy> RolePolicies,
    IReadOnlyList<string> HostSchemes,
    string? LoginUrl,
    string? LogoutUrl)
{
    /// <summary>The mode reported to the panel.</summary>
    public DashboardAccessMode EffectiveMode => Mode ?? DashboardAccessMode.Localhost;
}

/// <summary>Identifies the user of a request in the configured mode (ADR-0021). The result is cached for the request.</summary>
internal sealed class DashboardAccess(DashboardAccessSettings settings)
{
    public const string SessionScheme = "PythonScriptRunner.Dashboard.Session";
    public const string RoleClaim = "psr:role";
    public const string StampClaim = "psr:stamp";

    private static readonly object CacheKey = new();

    public async ValueTask<DashboardPrincipal?> ResolveAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Items.TryGetValue(CacheKey, out var cached))
        {
            return (DashboardPrincipal?)cached;
        }

        var principal = await ResolveCoreAsync(http).ConfigureAwait(false);
        http.Items[CacheKey] = principal;
        return principal;
    }

    /// <summary>Whether the request comes from this machine (the real address, after the host ForwardedHeaders).</summary>
    public static bool IsLocal(HttpContext http)
    {
        var remote = http.Connection.RemoteIpAddress;
        return remote is not null && (IPAddress.IsLoopback(remote) || remote.Equals(http.Connection.LocalIpAddress));
    }

    private async ValueTask<DashboardPrincipal?> ResolveCoreAsync(HttpContext http)
    {
        if (settings.AllowLocalRequests && IsLocal(http))
        {
            return new DashboardPrincipal("local", "local", DashboardRole.Admin, DashboardAccessMode.Localhost);
        }

        switch (settings.Mode)
        {
            case DashboardAccessMode.FixedLogin:
                {
                    var result = await http.AuthenticateAsync(SessionScheme).ConfigureAwait(false);
                    if (!result.Succeeded || result.Principal.Identity?.Name is not { } name
                        || !Enum.TryParse<DashboardRole>(result.Principal.FindFirstValue(RoleClaim), out var role))
                    {
                        return null;
                    }

                    return new DashboardPrincipal($"login:{name}", name, role, DashboardAccessMode.FixedLogin);
                }

            case DashboardAccessMode.Filter:
                {
                    var filter = (IDashboardAuthorizationFilter)http.RequestServices.GetRequiredService(settings.FilterType!);
                    var user = await filter.AuthorizeAsync(http).ConfigureAwait(false);
                    return user is null || string.IsNullOrWhiteSpace(user.Name)
                        ? null
                        : new DashboardPrincipal($"filter:{user.Name}", user.Name, user.Role, DashboardAccessMode.Filter);
                }

            case DashboardAccessMode.Host:
                return await ResolveHostAsync(http).ConfigureAwait(false);

            default:
                return null;
        }
    }

    private async ValueTask<DashboardPrincipal?> ResolveHostAsync(HttpContext http)
    {
        var user = http.User;
        foreach (var scheme in settings.HostSchemes)
        {
            var result = await http.AuthenticateAsync(scheme).ConfigureAwait(false);
            if (result.Succeeded)
            {
                user = result.Principal;
                break;
            }
        }

        if (user.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var subject = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.Identity.Name ?? "unknown";
        var name = user.Identity.Name ?? user.FindFirstValue("name") ?? subject;
        // Resolved per request: host handlers may be scoped.
        var authorization = http.RequestServices.GetRequiredService<IAuthorizationService>();
        foreach (var role in (DashboardRole[])[DashboardRole.Admin, DashboardRole.Author, DashboardRole.Executor, DashboardRole.Reader])
        {
            if ((await authorization.AuthorizeAsync(user, http, settings.RolePolicies[role]).ConfigureAwait(false)).Succeeded)
            {
                return new DashboardPrincipal(subject, name, role, DashboardAccessMode.Host);
            }
        }

        return new DashboardPrincipal(subject, name, null, DashboardAccessMode.Host);
    }
}

/// <summary>Requires at least <see cref="Minimum"/> (roles are cumulative; D-28), or only an identified user when <c>null</c>.</summary>
/// <param name="Minimum">Lowest role allowed.</param>
internal sealed record DashboardRoleRequirement(DashboardRole? Minimum) : IAuthorizationRequirement
{
    /// <summary>Policy of any identified user, even without a role (<c>/me</c>, logout).</summary>
    public const string UserPolicy = "psr:user";

    /// <summary>Name of the policy of a role (<c>psr:reader</c>, <c>psr:executor</c>, <c>psr:author</c>, <c>psr:admin</c>).</summary>
    public static string PolicyName(DashboardRole role) => $"psr:{role.ToString().ToLowerInvariant()}";
}

/// <summary>Evaluates <see cref="DashboardRoleRequirement"/> with the dashboard user of the request.</summary>
internal sealed class DashboardRoleHandler(DashboardAccess access) : AuthorizationHandler<DashboardRoleRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, DashboardRoleRequirement requirement)
    {
        if (context.Resource is HttpContext http && await access.ResolveAsync(http).ConfigureAwait(false) is { } user
            && (requirement.Minimum is not { } minimum || user.Role >= minimum))
        {
            context.Succeed(requirement);
        }
    }
}

/// <summary>Marks the endpoints of the dashboard (API and hub): their failures become JSON 401/403 instead of host challenges.</summary>
internal sealed class DashboardEndpointMetadata;
