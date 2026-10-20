using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using PythonScriptRunner;
using PythonScriptRunner.Dashboard;
using PythonScriptRunner.Dashboard.Access;
using PythonScriptRunner.Dashboard.Api;
using PythonScriptRunner.Dashboard.Realtime;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the embeddable dashboard of PythonScriptRunner.</summary>
public static class DashboardServiceCollectionExtensions
{
    /// <summary>
    /// Adds the dashboard (spec 007; requires <c>AddEntityFrameworkCore</c> and <c>AddRabbitMq</c>): internal API, real-time
    /// updates and the panel, protected by exactly one access mode — fixed login, custom filter or host authentication — and,
    /// in Development only, local requests (ADR-0021). Map it with <c>app.MapPythonScriptRunnerDashboard("/psr")</c>.
    /// </summary>
    /// <param name="builder">Builder returned by <c>AddPythonScriptRunner</c>.</param>
    /// <param name="configure">Access mode.</param>
    /// <exception cref="PsrConfigurationException">No access mode, more than one, or an incomplete one.</exception>
    public static IPythonScriptRunnerBuilder AddDashboard(this IPythonScriptRunnerBuilder builder, Action<DashboardOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var services = builder.Services;
        if (!services.Any(d => d.ServiceType == typeof(IRunQueue)))
        {
            throw new PsrConfigurationException(
                "AddDashboard requires the queue: call AddEntityFrameworkCore(...) and AddRabbitMq(...) first (tests and runs go to the workers).");
        }

        if (services.Any(d => d.ServiceType == typeof(DashboardAccessSettings)))
        {
            throw new PsrConfigurationException("AddDashboard was called more than once.");
        }

        var options = new DashboardOptions();
        configure(options);
        var settings = BuildSettings(options);

        services.AddSingleton(settings);
        services.AddSingleton<DashboardRoutes>();
        services.AddSingleton<DashboardAccess>();
        services.AddSingleton<OverviewQuery>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, DashboardRoleHandler>());
        var authorization = services.AddAuthorizationBuilder()
            .AddPolicy(Policies.User, p => p.AddRequirements(new DashboardRoleRequirement(null)));
        foreach (var role in Enum.GetValues<DashboardRole>())
        {
            authorization.AddPolicy(DashboardRoleRequirement.PolicyName(role), p => p.AddRequirements(new DashboardRoleRequirement(role)));
        }

        DecorateAuthorizationResults(services);
        switch (settings.Mode)
        {
            case DashboardAccessMode.FixedLogin:
                AddFixedLogin(services, options.FixedLogin);
                break;
            case DashboardAccessMode.Filter:
                services.TryAddScoped(settings.FilterType!);
                break;
        }

        services.AddSignalR();
        services.AddOpenApi(DashboardOpenApi.Document, DashboardOpenApi.Configure);
        builder.AddRunEventHandler<RunEventBridge>(s => s.PerInstance("dashboard"));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DashboardStartupCheck>());
        return builder;
    }

    private static DashboardAccessSettings BuildSettings(DashboardOptions options)
    {
        var errors = new List<string>();
        if (options.MainModes > 1)
        {
            errors.Add("choose only one access mode (UseFixedLogin, UseAuthorizationFilter or UseHostAuthentication)");
        }

        if (options.Mode is null && !options.AllowLocalRequests)
        {
            errors.Add("no access mode: call UseFixedLogin, UseAuthorizationFilter or UseHostAuthentication (or allow local requests in Development)");
        }

        var policies = new Dictionary<DashboardRole, AuthorizationPolicy>();
        var host = options.HostAuthentication;
        if (options.Mode == DashboardAccessMode.Host)
        {
            foreach (var role in Enum.GetValues<DashboardRole>())
            {
                var configure = role switch
                {
                    DashboardRole.Reader => host.Reader,
                    DashboardRole.Executor => host.Executor,
                    DashboardRole.Author => host.Author,
                    _ => host.Admin,
                };
                if (configure is null)
                {
                    errors.Add($"host authentication needs the policy of every role; {role} is missing");
                    continue;
                }

                try
                {
                    var policy = new AuthorizationPolicyBuilder();
                    configure(policy);
                    policies[role] = policy.Build();
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add($"the policy of {role} is invalid: {ex.Message}");
                }
            }
        }

        if (options.Mode == DashboardAccessMode.FixedLogin && options.FixedLogin.ConfigurationSection is null)
        {
            // Users in code are checked now; users from configuration are checked at startup.
            var problems = FixedLoginUsers.Errors(FixedLoginSettingsFrom(options.FixedLogin));
            errors.AddRange(problems.Select(p => $"fixed login: {p}"));
        }

        return errors.Count > 0
            ? throw new PsrConfigurationException($"Invalid dashboard configuration: {string.Join("; ", errors)}.")
            : new DashboardAccessSettings(options.Mode, options.AllowLocalRequests, options.FilterType, policies, [.. host.AuthenticationSchemes],
                host.LoginUrl, host.LogoutUrl);
    }

    private static void AddFixedLogin(IServiceCollection services, FixedLoginOptions login)
    {
        var settings = services.AddOptions<FixedLoginSettings>().Configure(s => Copy(login, s, users: login.ConfigurationSection is null));
        if (login.ConfigurationSection is { } section)
        {
            settings.BindConfiguration(section);
        }

        services.TryAddSingleton<FixedLoginUsers>();
        services.TryAddSingleton<LoginThrottle>();
        services.TryAddSingleton<DashboardKeys>();
        services.TryAddSingleton<DashboardDataProtection>();
        services.AddAuthentication().AddCookie(DashboardAccess.SessionScheme);
        services.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>>(provider => new SessionCookieOptions(
            provider.GetRequiredService<DashboardRoutes>(), provider.GetRequiredService<FixedLoginUsers>(), provider.GetRequiredService<TimeProvider>(),
            provider, login.PersistKeysInDatabase));
    }

    private static FixedLoginSettings FixedLoginSettingsFrom(FixedLoginOptions login)
    {
        var settings = new FixedLoginSettings();
        Copy(login, settings, users: true);
        return settings;
    }

    private static void Copy(FixedLoginOptions login, FixedLoginSettings settings, bool users)
    {
        settings.SlidingExpiration = login.SlidingExpiration;
        settings.AbsoluteExpiration = login.AbsoluteExpiration;
        settings.MaxFailedAttempts = login.MaxFailedAttempts;
        settings.LockoutDuration = login.LockoutDuration;
        settings.MaxAttemptsPerMinutePerIp = login.MaxAttemptsPerMinutePerIp;
        if (users)
        {
            settings.Users = [.. login.Users.Select(u => new FixedLoginUser { Name = u.Name, Role = u.Role, PasswordHash = u.PasswordHash })];
        }
    }

    /// <summary>
    /// Wraps the authorization result handler of the host: dashboard endpoints answer failures with JSON 401/403, other endpoints
    /// keep the host behaviour.
    /// </summary>
    private static void DecorateAuthorizationResults(IServiceCollection services)
    {
        services.AddAuthorization();
        var previous = services.LastOrDefault(d => d.ServiceType == typeof(IAuthorizationMiddlewareResultHandler) && !d.IsKeyedService);
        services.RemoveAll<IAuthorizationMiddlewareResultHandler>();
        services.AddTransient<IAuthorizationMiddlewareResultHandler>(provider => new DashboardAuthorizationResultHandler(
            previous switch
            {
                { ImplementationInstance: IAuthorizationMiddlewareResultHandler instance } => instance,
                { ImplementationFactory: { } factory } => (IAuthorizationMiddlewareResultHandler)factory(provider),
                { ImplementationType: { } type } => (IAuthorizationMiddlewareResultHandler)ActivatorUtilities.CreateInstance(provider, type),
                _ => new AuthorizationMiddlewareResultHandler(),
            },
            provider.GetRequiredService<DashboardAccess>()));
    }
}

/// <summary>Checks at startup what depends on the environment or the configuration (D-122, D-134).</summary>
internal sealed class DashboardStartupCheck(IServiceProvider services, IHostEnvironment environment, DashboardAccessSettings settings) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.AllowLocalRequests && !environment.IsDevelopment())
        {
            throw new PsrConfigurationException(
                $"The dashboard allows local requests only in Development (environment: {environment.EnvironmentName}). Use a main access mode instead.");
        }

        if (settings.Mode == DashboardAccessMode.FixedLogin)
        {
            // Validates the users (from configuration) now instead of on the first login.
            _ = services.GetRequiredService<FixedLoginUsers>();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
