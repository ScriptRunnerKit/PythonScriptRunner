using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PythonScriptRunner.Dashboard.Access;
using PythonScriptRunner.EntityFrameworkCore;

namespace PythonScriptRunner.Dashboard.Api;

/// <summary>Audit, secret status, settings and field types (spec 007).</summary>
internal static class AdminEndpoints
{
    private static readonly FieldTypesResponse FieldTypes = BuildFieldTypes();

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/audit", AuditAsync).RequireAuthorization(Policies.Admin).WithTags("Administration").WithName("ListAudit")
            .Produces<KeysetPage<AuditEvent>>().ProducesProblem(StatusCodes.Status400BadRequest);
        api.MapGet("/admin/secrets", SecretsAsync).RequireAuthorization(Policies.Admin).WithTags("Administration").WithName("ListSecrets")
            .Produces<IReadOnlyList<SecretStatus>>();
        api.MapGet("/admin/settings", Settings).RequireAuthorization(Policies.Admin).WithTags("Administration").WithName("GetSettings")
            .Produces<SettingsResponse>();
        api.MapGet("/meta/field-types", () => FieldTypes).RequireAuthorization(Policies.Reader).WithTags("Meta").WithName("GetFieldTypes")
            .Produces<FieldTypesResponse>();
    }

    private static Task<KeysetPage<AuditEvent>> AuditAsync([AsParameters] AuditListQuery query, IAuditReader audit, CancellationToken cancellationToken) =>
        audit.ListAsync(new AuditQuery
        {
            Actor = query.Actor,
            Action = query.Action,
            EntityType = query.EntityType,
            EntityKey = query.EntityKey,
            From = query.From,
            To = query.To,
            Cursor = query.Cursor,
            Limit = query.Limit ?? 50,
        }, cancellationToken);

    /// <summary>Secrets declared by stage versions that are not archived, and whether they resolve — never their values (AC-16).</summary>
    private static async Task<IReadOnlyList<SecretStatus>> SecretsAsync(
        IDbContextFactory<PsrDbContext> factory, ISecretProvider secrets, CancellationToken cancellationToken)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        List<(string Secret, string DeclaredBy)> declarations;
        await using (db.ConfigureAwait(false))
        {
            var rows = await db.StageVersions.AsNoTracking()
                .Where(v => v.Status != VersionStatus.Archived && v.RequiredSecrets.Length > 0)
                .Select(v => new { v.Stage.Key, v.Version, v.RequiredSecrets })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            declarations = [.. rows.SelectMany(r => r.RequiredSecrets.Select(s => (s, $"{r.Key}@{r.Version}")))];
        }

        var result = new List<SecretStatus>();
        foreach (var group in declarations.GroupBy(d => d.Secret, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var resolved = await secrets.GetSecretAsync(group.Key, cancellationToken).ConfigureAwait(false) is not null;
            result.Add(new SecretStatus(group.Key, [.. group.Select(d => d.DeclaredBy).Distinct().Order(StringComparer.Ordinal)], resolved));
        }

        return result;
    }

    private static SettingsResponse Settings(
        HttpContext http, PsrEntityFrameworkOptions persistence, IOptions<PythonScriptRunnerOptions> runner, DashboardAccessSettings access, DashboardRoutes routes)
    {
        var options = runner.Value;
        return new SettingsResponse(
            RetentionSettings.From(persistence.Retention),
            new LimitSettings(options.MaxFileBytes, options.MaxRunFilesBytes, options.MaxLogBytes, options.DefaultTimeout, options.DefaultMemoryLimitMb,
                options.MaxConcurrentProcesses, options.DefaultPythonVersion),
            AuthEndpoints.Info(http, access, routes),
            new Dictionary<string, string>
            {
                ["pythonScriptRunner"] = VersionOf(typeof(AdminEndpoints).Assembly),
                ["dotnet"] = Environment.Version.ToString(),
                ["psrunner"] = options.PsrunnerRequirement,
            });
    }

    private static string VersionOf(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "0.0.0";
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? version : version[..metadata];
    }

    /// <summary>The 12 types with the widgets of spec 008 and the binding compatibility (spec 003).</summary>
    private static FieldTypesResponse BuildFieldTypes()
    {
        (FieldType Type, string Input, string Output)[] types =
        [
            (FieldType.Number, "number", "number"),
            (FieldType.Integer, "number", "number"),
            (FieldType.Decimal, "decimal-text", "number"),
            (FieldType.String, "text", "text-copy"),
            (FieldType.Bool, "switch", "chip"),
            (FieldType.Date, "date-picker", "date"),
            (FieldType.DateTime, "datetime-picker", "datetime"),
            (FieldType.Enum("a", "b"), "select", "chip"),
            (FieldType.Json, "json-editor", "json-tree"),
            (FieldType.Table, "json-editor-csv", "data-grid"),
            (FieldType.File, "file-upload", "file-card"),
            (FieldType.ListOf(FieldType.String), "repeatable", "list"),
        ];
        var infos = types.Select(t => new FieldTypeInfo(t.Type.WireName, t.Type.IsScalar, t.Input, t.Output,
            t.Type.Kind is not (FieldKind.File or FieldKind.Table) && !t.Type.IsFile)).ToList();
        var compatibility = types.ToDictionary(
            source => source.Type.WireName,
            source => (IReadOnlyList<string>)[.. types
                .Where(target => FieldCompatibility.IsAssignable(new FieldDefinition("source", source.Type), new FieldDefinition("target", target.Type)))
                .Select(target => target.Type.WireName)]);
        return new FieldTypesResponse(infos, compatibility,
        [
            "integer feeds number and decimal; enum feeds string; any non-file type feeds json.",
            "enum feeds enum when every source value is a target value.",
            "table feeds table when every declared target column exists in the source with a compatible type.",
            "file feeds file when the declared content types intersect (no declaration accepts any).",
            "list<T> feeds list<U> when T feeds U.",
        ]);
    }
}

/// <summary>Filters of <c>GET /audit</c> (camelCase in the query, like the rest of the API).</summary>
internal sealed record AuditListQuery(
    [FromQuery(Name = "actor")] string? Actor,
    [FromQuery(Name = "action")] string? Action,
    [FromQuery(Name = "entityType")] string? EntityType,
    [FromQuery(Name = "entityKey")] string? EntityKey,
    [FromQuery(Name = "from")] DateTimeOffset? From,
    [FromQuery(Name = "to")] DateTimeOffset? To,
    [FromQuery(Name = "cursor")] string? Cursor,
    [FromQuery(Name = "limit")] int? Limit);
