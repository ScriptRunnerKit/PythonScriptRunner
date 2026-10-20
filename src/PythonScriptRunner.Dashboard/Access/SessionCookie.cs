using System.Globalization;
using System.Security.Claims;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PythonScriptRunner.EntityFrameworkCore;
using PythonScriptRunner.EntityFrameworkCore.Entities;

namespace PythonScriptRunner.Dashboard.Access;

/// <summary>Where the dashboard is mapped (set by <c>MapPythonScriptRunnerDashboard</c>; cookie options are built later, on first use).</summary>
internal sealed class DashboardRoutes
{
    public string Prefix { get; set; } = "/psr";

    public bool Mapped { get; set; }
}

/// <summary>
/// Session cookie of the fixed login (D-120, D-127, D-128): <c>psr.session</c>, HttpOnly, <c>SameSite=Lax</c>, <c>Secure</c> on
/// HTTPS, path = prefix; 8 h sliding and 24 h absolute. Each request checks that the user still exists with the same password
/// and takes its current role.
/// </summary>
internal sealed class SessionCookieOptions(
    DashboardRoutes routes, FixedLoginUsers users, TimeProvider time, IServiceProvider services, bool persistKeysInDatabase)
    : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public const string CookieName = "psr.session";

    /// <summary>Login time, kept in the ticket across renewals (absolute expiration).</summary>
    public const string LoginAtItem = "psr.loginAt";

    private static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(1);

    public void Configure(CookieAuthenticationOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (name != DashboardAccess.SessionScheme)
        {
            return;
        }

        options.Cookie = new PrefixCookieBuilder(routes)
        {
            Name = CookieName,
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            SecurePolicy = CookieSecurePolicy.SameAsRequest,
            IsEssential = true,
        };
        options.ExpireTimeSpan = users.Settings.SlidingExpiration;
        options.SlidingExpiration = false;
        options.TimeProvider = time;
        if (persistKeysInDatabase)
        {
            options.DataProtectionProvider = services.GetRequiredService<DashboardDataProtection>().Provider;
        }

        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            },
            OnValidatePrincipal = Validate,
        };
    }

    private async Task Validate(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var account = users.Find(principal?.Identity?.Name);
        var now = time.GetUtcNow();
        if (account is null || principal!.FindFirstValue(DashboardAccess.StampClaim) != account.Stamp
            || LoginAt(context.Properties) is not { } loginAt || now - loginAt >= users.Settings.AbsoluteExpiration)
        {
            // Removed user, changed password or past the absolute expiration (D-127, D-128).
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(DashboardAccess.SessionScheme).ConfigureAwait(false);
            return;
        }

        if (principal!.FindFirstValue(DashboardAccess.RoleClaim) != account.Role.ToString())
        {
            // A changed role applies at once (D-128).
            context.ReplacePrincipal(CreatePrincipal(account));
            context.ShouldRenew = true;
        }

        // Exact sliding expiration: the idle time counts from the last request (renewed at most once a minute).
        if (context.Properties.IssuedUtc is { } issued && now - issued >= RenewAfter)
        {
            context.ShouldRenew = true;
        }
    }

    public static ClaimsPrincipal CreatePrincipal(LoginAccount account) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, account.Name),
            new Claim(DashboardAccess.RoleClaim, account.Role.ToString()),
            new Claim(DashboardAccess.StampClaim, account.Stamp),
        ],
        DashboardAccess.SessionScheme));

    public static AuthenticationProperties CreateProperties(DateTimeOffset now) => new(new Dictionary<string, string?>
    {
        [LoginAtItem] = now.UtcTicks.ToString(CultureInfo.InvariantCulture),
    })
    {
        IsPersistent = false,
    };

    private static DateTimeOffset? LoginAt(AuthenticationProperties properties) =>
        properties.Items.TryGetValue(LoginAtItem, out var text) && long.TryParse(text, CultureInfo.InvariantCulture, out var ticks)
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;

    /// <summary>Cookie path = path base + prefix, so a host mounted under a path base still scopes the cookie to the dashboard (AC-14).</summary>
    private sealed class PrefixCookieBuilder(DashboardRoutes routes) : CookieBuilder
    {
        public override CookieOptions Build(HttpContext context, DateTimeOffset expiresFrom)
        {
            var options = base.Build(context, expiresFrom);
            options.Path = context.Request.PathBase.Add(routes.Prefix).Value;
            return options;
        }
    }
}

/// <summary>
/// Data Protection dedicated to the session cookie, with its keys in <c>psr.dashboard_keys</c> (D-132): every instance and
/// restart reads the same keys. The Data Protection of the host is not changed.
/// </summary>
internal sealed class DashboardDataProtection(DashboardKeys keys, ILoggerFactory loggers) : IDisposable
{
    public const string ApplicationName = "PythonScriptRunner.Dashboard";

    private readonly Lazy<ServiceProvider> _services = new(() =>
    {
        var collection = new ServiceCollection();
        collection.AddSingleton(loggers);
        collection.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .AddKeyManagementOptions(o => o.XmlRepository = keys);
        return collection.BuildServiceProvider();
    });

    public IDataProtectionProvider Provider => _services.Value.GetRequiredService<IDataProtectionProvider>();

    public void Dispose()
    {
        if (_services.IsValueCreated)
        {
            _services.Value.Dispose();
        }
    }
}

/// <summary>Key ring of <see cref="DashboardDataProtection"/> stored in <c>psr.dashboard_keys</c>.</summary>
internal sealed class DashboardKeys(IDbContextFactory<PsrDbContext> factory, TimeProvider time) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var db = factory.CreateDbContext();
        return [.. db.DashboardKeys.AsNoTracking().OrderBy(k => k.Id).Select(k => k.Xml).ToList().Select(XElement.Parse)];
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        using var db = factory.CreateDbContext();
        db.DashboardKeys.Add(new DashboardKeyEntity
        {
            FriendlyName = friendlyName,
            Xml = element.ToString(SaveOptions.DisableFormatting),
            CreatedAt = time.GetUtcNow(),
        });
        db.SaveChanges();
    }
}
