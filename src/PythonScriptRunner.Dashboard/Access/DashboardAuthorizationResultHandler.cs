using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace PythonScriptRunner.Dashboard.Access;

/// <summary>
/// For dashboard endpoints, an authorization failure is answered with JSON — 401 when nobody is identified, 403 when the role is
/// not enough — instead of the host's challenge (which could redirect to an identity provider). Other endpoints keep the
/// handler the host had.
/// </summary>
internal sealed class DashboardAuthorizationResultHandler(IAuthorizationMiddlewareResultHandler inner, DashboardAccess access) : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Succeeded || context.GetEndpoint()?.Metadata.GetMetadata<DashboardEndpointMetadata>() is null)
        {
            await inner.HandleAsync(next, context, policy, authorizeResult).ConfigureAwait(false);
            return;
        }

        var user = await access.ResolveAsync(context).ConfigureAwait(false);
        var (status, code, title) = user is null
            ? (StatusCodes.Status401Unauthorized, "UNAUTHENTICATED", "Authentication is required.")
            : (StatusCodes.Status403Forbidden, "FORBIDDEN", "The role of the user does not allow this action.");
        await ApiProblems.WriteAsync(context, status, code, title).ConfigureAwait(false);
    }
}
