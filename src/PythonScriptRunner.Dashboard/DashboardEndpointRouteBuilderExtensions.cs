using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using PythonScriptRunner;
using PythonScriptRunner.Dashboard;
using PythonScriptRunner.Dashboard.Access;
using PythonScriptRunner.Dashboard.Api;
using PythonScriptRunner.Dashboard.Realtime;
using PythonScriptRunner.Dashboard.Spa;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps the embeddable dashboard of PythonScriptRunner.</summary>
public static class DashboardEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the dashboard under <paramref name="prefix"/>: API in <c>{prefix}/api</c>, real-time updates in
    /// <c>{prefix}/hubs/runs</c> and the panel in <c>{prefix}/</c> (spec 007). Without this call there is no dashboard.
    /// </summary>
    /// <param name="endpoints">The application.</param>
    /// <param name="prefix">Path of the dashboard (e.g. <c>/psr</c> or <c>/admin/python</c>).</param>
    /// <returns>A builder to add conventions to every dashboard endpoint (e.g. <c>RequireHost</c>).</returns>
    /// <exception cref="PsrConfigurationException"><c>AddDashboard</c> was not called, or the dashboard is already mapped.</exception>
    public static IEndpointConventionBuilder MapPythonScriptRunnerDashboard(this IEndpointRouteBuilder endpoints, string prefix = "/psr")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var normalized = "/" + prefix.Trim().Trim('/');
        if (normalized == "/" || normalized.Contains("//", StringComparison.Ordinal) || normalized.Contains('{', StringComparison.Ordinal))
        {
            throw new ArgumentException("The prefix must be a path below the root, such as /psr.", nameof(prefix));
        }

        var services = endpoints.ServiceProvider;
        if (services.GetService<DashboardAccessSettings>() is null)
        {
            throw new PsrConfigurationException("Call AddDashboard(...) on the PythonScriptRunner builder before MapPythonScriptRunnerDashboard.");
        }

        var routes = services.GetRequiredService<DashboardRoutes>();
        if (routes.Mapped)
        {
            throw new PsrConfigurationException("The dashboard is already mapped; call MapPythonScriptRunnerDashboard once.");
        }

        routes.Prefix = normalized;
        routes.Mapped = true;

        var root = endpoints.MapGroup(normalized);
        var api = root.MapGroup("/api")
            .WithMetadata(new DashboardEndpointMetadata())
            .AddEndpointFilter<ApiFilter>()
            .WithGroupName(DashboardOpenApi.Document);
        AuthEndpoints.Map(api);
        StageEndpoints.Map(api);
        PipelineEndpoints.Map(api);
        RunEndpoints.Map(api);
        ScheduleEndpoints.Map(api);
        AdminEndpoints.Map(api);

        if (services.GetRequiredService<IHostEnvironment>().IsDevelopment())
        {
            // Internal document, only in Development (D-26); its snapshot feeds the types of the panel.
            root.MapGet("/api/openapi/v1.json", async (HttpContext http, CancellationToken cancellationToken) =>
                {
                    var provider = http.RequestServices.GetRequiredKeyedService<IOpenApiDocumentProvider>(DashboardOpenApi.Document);
                    var document = await provider.GetOpenApiDocumentAsync(cancellationToken).ConfigureAwait(false);
                    return TypedResults.Text(await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_1, cancellationToken).ConfigureAwait(false),
                        "application/json; charset=utf-8");
                })
                .AllowAnonymous()
                .ExcludeFromDescription();
        }

        // Unknown API paths are 404 problems, not the panel page.
        root.MapMethods("/api/{**path}", ["GET", "POST", "PUT", "PATCH", "DELETE"], (HttpContext http) => ApiProblems.NotFound(http, "Unknown dashboard API path."))
            .AllowAnonymous()
            .ExcludeFromDescription();

        root.MapHub<RunsHub>("/hubs/runs")
            .RequireAuthorization(Policies.Reader)
            .WithMetadata(new DashboardEndpointMetadata());
        DashboardSpa.Map(root);
        return root;
    }
}
