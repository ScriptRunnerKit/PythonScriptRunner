using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace PythonScriptRunner.Dashboard.Spa;

/// <summary>
/// Serves the panel embedded in the package (spec 008; a placeholder page until phase 6): static files as they are, every other
/// path gets <c>index.html</c> with <c>&lt;base href&gt;</c> pointing at the prefix (AC-14) and the security headers of spec 007.
/// </summary>
internal static partial class DashboardSpa
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; worker-src 'self' blob:; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'";

    private static readonly IFileProvider Files = new ManifestEmbeddedFileProvider(typeof(DashboardSpa).Assembly, "wwwroot");
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private static readonly Lazy<string> Index = new(() =>
    {
        using var reader = new StreamReader(Files.GetFileInfo("index.html").CreateReadStream(), Encoding.UTF8);
        return reader.ReadToEnd();
    });

    [GeneratedRegex("<base\\s[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BaseTag();

    public static void Map(RouteGroupBuilder root)
    {
        root.MapGet("/{**path}", Serve).AllowAnonymous().ExcludeFromDescription();
    }

    private static IResult Serve(string? path, HttpContext http)
    {
        SetSecurityHeaders(http.Response);
        if (path is { Length: > 0 } && !path.Equals("index.html", StringComparison.OrdinalIgnoreCase)
            && Files.GetFileInfo(path) is { Exists: true, IsDirectory: false } file)
        {
            if (!ContentTypes.TryGetContentType(path, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            // Vite names the built assets by hash: they never change.
            http.Response.Headers.CacheControl = path.StartsWith("assets/", StringComparison.Ordinal) ? "public, max-age=31536000, immutable" : "no-cache";
            return TypedResults.Stream(file.CreateReadStream(), contentType);
        }

        http.Response.Headers.CacheControl = "no-cache";
        var root = $"{http.Request.PathBase}{RootOf(http)}/";
        var tag = $"<base href=\"{WebUtility.HtmlEncode(root)}\">";
        var html = BaseTag().IsMatch(Index.Value)
            ? BaseTag().Replace(Index.Value, tag, 1)
            : Index.Value.Replace("<head>", "<head>" + tag, StringComparison.OrdinalIgnoreCase);
        return TypedResults.Content(html, "text/html; charset=utf-8");
    }

    private static void SetSecurityHeaders(HttpResponse response)
    {
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "same-origin";
    }

    private static string RootOf(HttpContext http) =>
        http.RequestServices.GetService(typeof(Access.DashboardRoutes)) is Access.DashboardRoutes routes ? routes.Prefix : "/psr";
}
