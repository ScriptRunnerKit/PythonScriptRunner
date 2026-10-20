using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

// Standalone dashboard of PythonScriptRunner (spec 009): API, real-time updates and panel of spec 007, plus the outbox relay.
// Fixed login by default, with users from environment variables (PythonScriptRunner__Dashboard__Login__Users__0__Name, __Role,
// __PasswordHash); OIDC when Oidc__Authority is set (D-125). Also: ConnectionStrings__psr, ConnectionStrings__rabbitmq,
// Dashboard__PathPrefix (default /psr), PSR_AUTO_MIGRATE and ASPNETCORE_FORWARDEDHEADERS_ENABLED behind a proxy.
var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults("psr-dashboard");

var configuration = builder.Configuration;
var prefix = configuration["Dashboard:PathPrefix"] is { Length: > 0 } path ? "/" + path.Trim('/') : "/psr";
var oidc = configuration.GetSection("Oidc");
var useOidc = !string.IsNullOrWhiteSpace(oidc["Authority"]);
if (!useOidc && !configuration.GetSection("PythonScriptRunner:Dashboard:Login:Users").GetChildren().Any())
{
    throw new InvalidOperationException(
        "The dashboard has no way to sign in: set the fixed login users (PythonScriptRunner__Dashboard__Login__Users__0__Name, __Role and "
        + "__PasswordHash, with the hash from 'psr hash-password') or enable OIDC (Oidc__Authority, Oidc__ClientId and Oidc__ClientSecret).");
}

var roleClaim = configuration["Dashboard:RoleClaim"] ?? "roles";
if (useOidc)
{
    // Host authentication (BFF): the cookie keeps the session, OpenID Connect signs in; tokens never reach the browser.
    builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        })
        .AddCookie(o =>
        {
            o.Cookie.Name = "psr.host";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
        })
        .AddOpenIdConnect(o =>
        {
            o.Authority = oidc["Authority"];
            o.ClientId = oidc["ClientId"];
            o.ClientSecret = oidc["ClientSecret"];
            o.RequireHttpsMetadata = oidc.GetValue("RequireHttpsMetadata", true);
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.UsePkce = true;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = true;
            o.MapInboundClaims = false;
            o.Scope.Add("profile");
            o.TokenValidationParameters.NameClaimType = "name";
            o.TokenValidationParameters.RoleClaimType = roleClaim;
        });
}

builder.Services
    .AddPythonScriptRunner(o => configuration.GetSection("PythonScriptRunner").Bind(o))
    .AddEntityFrameworkCore(o =>
    {
        configuration.GetSection("PythonScriptRunner:EntityFrameworkCore").Bind(o);
        o.UseNpgsql(configuration.GetConnectionString("psr")
            ?? throw new InvalidOperationException("Set the PostgreSQL connection string ConnectionStrings__psr."));
    })
    .AddRabbitMq(o =>
    {
        configuration.GetSection("PythonScriptRunner:RabbitMQ").Bind(o);
        o.ConnectionString = configuration.GetConnectionString("rabbitmq")
            ?? throw new InvalidOperationException("Set the RabbitMQ connection string ConnectionStrings__rabbitmq.");
    })
    .AddDashboard(o =>
    {
        if (!useOidc)
        {
            o.UseFixedLogin(login => login.BindConfiguration());
            return;
        }

        // Each role needs its own claim value (Dashboard__Roles__Reader...); the highest one the user has wins.
        string Role(string name, string fallback) => configuration[$"Dashboard:Roles:{name}"] ?? fallback;
        o.UseHostAuthentication(roles =>
        {
            roles.Reader = p => p.RequireAuthenticatedUser().RequireClaim(roleClaim, Role("Reader", "psr-leitor"));
            roles.Executor = p => p.RequireAuthenticatedUser().RequireClaim(roleClaim, Role("Executor", "psr-executor"));
            roles.Author = p => p.RequireAuthenticatedUser().RequireClaim(roleClaim, Role("Author", "psr-autor"));
            roles.Admin = p => p.RequireAuthenticatedUser().RequireClaim(roleClaim, Role("Admin", "psr-admin"));
            roles.LoginUrl = "/account/login?returnUrl={returnUrl}";
            roles.LogoutUrl = "/account/logout";
        });
    });
string[] ready = [ServiceDefaultsExtensions.ReadyTag];
builder.Services.AddHealthChecks().AddPythonScriptRunnerDatabase(tags: ready).AddPythonScriptRunnerBroker(tags: ready);

var app = builder.Build();
if (configuration.GetValue<bool>("PSR_AUTO_MIGRATE"))
{
    await app.Services.MigratePythonScriptRunnerAsync();
}

app.UseAuthentication();
app.UseAuthorization();
if (useOidc)
{
    // The panel itself is challenged (redirect to the provider); the API answers 401 JSON and the hub refuses (spec 009, AC-10).
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments(prefix, out var rest)
            && !rest.StartsWithSegments("/api") && !rest.StartsWithSegments("/hubs")
            && context.User.Identity?.IsAuthenticated != true)
        {
            await context.ChallengeAsync(new AuthenticationProperties { RedirectUri = context.Request.PathBase + context.Request.Path + context.Request.QueryString });
            return;
        }

        await next(context);
    });
    app.MapGet("/account/login", (string? returnUrl) =>
        TypedResults.Challenge(new AuthenticationProperties { RedirectUri = IsLocal(returnUrl) ? returnUrl : prefix + "/" }));
    app.MapGet("/account/logout", () => TypedResults.SignOut(new AuthenticationProperties { RedirectUri = prefix + "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
}

app.MapDefaultEndpoints();
app.MapPythonScriptRunnerDashboard(prefix);
await app.RunAsync();

// Only paths of this site are accepted as return addresses (no open redirect).
static bool IsLocal(string? url) =>
    url is { Length: > 0 } && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

/// <summary>Entry point (public for the integration tests).</summary>
public partial class Program;
