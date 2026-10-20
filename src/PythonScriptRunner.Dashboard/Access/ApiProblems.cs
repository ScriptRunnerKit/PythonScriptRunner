using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace PythonScriptRunner.Dashboard;

/// <summary>Errors of the API as <c>ProblemDetails</c> (RFC 9457) with the extensions <c>code</c>, <c>errors</c>, <c>current</c> and <c>traceId</c>.</summary>
internal static class ApiProblems
{
    public static ProblemDetails Create(HttpContext context, int status, string code, string title, string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Type = $"https://httpstatuses.io/{status}",
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier;
        return problem;
    }

    public static IResult Result(HttpContext context, int status, string code, string title, string? detail = null, Action<ProblemDetails>? extend = null)
    {
        var problem = Create(context, status, code, title, detail);
        extend?.Invoke(problem);
        return TypedResults.Problem(problem);
    }

    public static IResult NotFound(HttpContext context, string detail) =>
        Result(context, StatusCodes.Status404NotFound, "NOT_FOUND", "The resource was not found.", detail);

    public static Task WriteAsync(HttpContext context, int status, string code, string title, string? detail = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(Create(context, status, code, title, detail), options: null, contentType: "application/problem+json");
    }

    /// <summary>Maps a domain exception to its status (spec 007); <c>null</c> for unexpected exceptions.</summary>
    public static IResult? FromException(HttpContext context, Exception exception) => exception switch
    {
        InputValidationException invalid => Result(context, StatusCodes.Status400BadRequest, "INPUT_INVALID", "The inputs do not match the contract.",
            invalid.Message, p => p.Extensions["errors"] = invalid.Errors
                .GroupBy(e => e.Field)
                .ToDictionary(g => g.Key, g => g.Select(e => new { e.Code, e.Message }).ToArray())),
        ConcurrencyConflictException conflict => Result(context, StatusCodes.Status412PreconditionFailed, conflict.Code, "The resource was changed by someone else.",
            conflict.Message, p => p.Extensions["current"] = conflict.Current),
        ScriptSyntaxException syntax => Result(context, StatusCodes.Status422UnprocessableEntity, syntax.Code, "The script does not compile.",
            syntax.Message, p =>
            {
                p.Extensions["line"] = syntax.Line;
                p.Extensions["column"] = syntax.Column;
            }),
        DefinitionInvalidException definition => Result(context,
            definition.Code == PsrErrorCodes.ImportInvalid ? StatusCodes.Status400BadRequest : StatusCodes.Status422UnprocessableEntity,
            definition.Code, "The definition is not valid.", definition.Message, p => p.Extensions["report"] = definition.Report),
        VersionReferencedException referenced => Result(context, StatusCodes.Status409Conflict, referenced.Code, "The version is in use.",
            referenced.Message, p => p.Extensions["pipelines"] = referenced.Pipelines),
        PsrDomainException domain => Result(context, StatusOf(domain.Code), domain.Code, Title(domain.Code), domain.Message),
        StageNotFoundException or PipelineNotFoundException or VersionNotFoundException or RunNotFoundException or ArtifactNotFoundException =>
            Result(context, StatusCodes.Status404NotFound, "NOT_FOUND", "The resource was not found.", exception.Message),
        VersionNotRunnableException => Result(context, StatusCodes.Status409Conflict, "VERSION_NOT_RUNNABLE", "The version cannot run.", exception.Message),
        PayloadTooLargeException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
            Result(context, StatusCodes.Status413PayloadTooLarge, "PAYLOAD_TOO_LARGE", "The upload is larger than allowed.", exception.Message),
        BadHttpRequestException bad => Result(context, bad.StatusCode, "BAD_REQUEST", "The request is not valid.", exception.Message),
        ArgumentException or JsonException or InvalidDataException =>
            Result(context, StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "The request is not valid.", exception.Message),
        _ => null,
    };

    private static int StatusOf(string code) => code switch
    {
        PsrErrorCodes.ConcurrencyConflict => StatusCodes.Status412PreconditionFailed,
        PsrErrorCodes.ArtifactExpired => StatusCodes.Status410Gone,
        PsrErrorCodes.ScheduleNotFound => StatusCodes.Status404NotFound,
        PsrErrorCodes.ScriptSyntaxError or PsrErrorCodes.DependencyResolutionFailed or PsrErrorCodes.StageInvalid or PsrErrorCodes.PipelineInvalid
            => StatusCodes.Status422UnprocessableEntity,
        PsrErrorCodes.RetentionExceedsGlobal or PsrErrorCodes.ScheduleFileInput or PsrErrorCodes.InvalidCron or PsrErrorCodes.InvalidTimeZone
            or PsrErrorCodes.ScheduleInvalid or PsrErrorCodes.ImportInvalid or PsrErrorCodes.FileInputNotAllowed => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status409Conflict,
    };

    private static string Title(string code) => code switch
    {
        PsrErrorCodes.ArtifactExpired => "The file is beyond the retention.",
        PsrErrorCodes.ScheduleNotFound => "The schedule was not found.",
        _ => "The operation is not allowed in the current state.",
    };
}

/// <summary>
/// Endpoint filter of the API: requires <c>X-PSR-Request: 1</c> on mutating requests (CSRF; QA-007-3) and turns domain
/// exceptions into <c>ProblemDetails</c>.
/// </summary>
internal sealed class ApiFilter : IEndpointFilter
{
    public const string CsrfHeader = "X-PSR-Request";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method) && !HttpMethods.IsOptions(http.Request.Method)
            && http.Request.Headers[CsrfHeader] != "1")
        {
            return ApiProblems.Result(http, StatusCodes.Status400BadRequest, "CSRF_HEADER_MISSING",
                $"Mutating requests must send the header {CsrfHeader}: 1.");
        }

        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ApiProblems.FromException(http, ex) is { } problem)
        {
            return problem;
        }
    }
}

/// <summary>An uploaded file or the whole upload exceeds the limits (413 before any run is created).</summary>
internal sealed class PayloadTooLargeException(string message) : Exception(message);

/// <summary>Optimistic concurrency with <c>ETag</c> = row version (<c>xmin</c>) and <c>If-Match</c>.</summary>
internal static class ETags
{
    public static string Of(uint rowVersion) => $"\"{rowVersion}\"";

    public static void Set(HttpContext context, uint rowVersion) => context.Response.Headers.ETag = Of(rowVersion);

    /// <summary>The row version sent in <c>If-Match</c>; <c>null</c> when missing or malformed.</summary>
    public static uint? IfMatch(HttpContext context) =>
        context.Request.Headers.IfMatch.ToString().Trim().Trim('"') is { Length: > 0 } text && uint.TryParse(text, out var version) ? version : null;

    public static IResult Required(HttpContext context) =>
        ApiProblems.Result(context, StatusCodes.Status428PreconditionRequired, "IF_MATCH_REQUIRED", "Send the ETag read before in If-Match.");
}
