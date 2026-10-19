using System.Text.RegularExpressions;
using PythonScriptRunner.Core.Values;

namespace PythonScriptRunner.Core.Catalog;

/// <summary>Validates a stage definition (same rules as <c>schemas/stage.v1.json</c> plus semantic checks).</summary>
internal static partial class StageDefinitionValidator
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    internal static partial Regex KeyPattern();

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,63}$")]
    internal static partial Regex FieldNamePattern();

    [GeneratedRegex("^[A-Z][A-Z0-9_]{0,63}$")]
    private static partial Regex SecretPattern();

    [GeneratedRegex(@"^3\.(1[0-9]|[2-9][0-9])$")]
    internal static partial Regex PythonVersionPattern();

    public static IReadOnlyList<string> Validate(StageDefinition stage)
    {
        var errors = new List<string>();
        if (stage.Key.Length is < 3 or > 64 || !KeyPattern().IsMatch(stage.Key))
        {
            errors.Add("key must be a slug of 3-64 chars (a-z, 0-9, '-')");
        }

        if (stage.Version < 1)
        {
            errors.Add("version must be >= 1");
        }

        if (string.IsNullOrWhiteSpace(stage.Name) || stage.Name.Length > 200)
        {
            errors.Add("name must have 1-200 chars");
        }

        if (stage.PythonVersion is not null && !PythonVersionPattern().IsMatch(stage.PythonVersion))
        {
            errors.Add($"python version '{stage.PythonVersion}' must look like 3.12 (minimum 3.10)");
        }

        switch (stage.Script)
        {
            case InlineScriptSource { Content: var content } when string.IsNullOrWhiteSpace(content):
                errors.Add("script is empty");
                break;
            case FileScriptSource { Path: var path } when !File.Exists(path):
                errors.Add($"script file not found: {path}");
                break;
        }

        if (stage.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1)))
        {
            errors.Add("timeout must be between 1 second and 24 hours");
        }

        if (stage.MemoryLimitMb is < 64)
        {
            errors.Add("memory limit must be at least 64 MB");
        }

        var retry = stage.Retry;
        if (retry.MaxAttempts is < 0 or > 20 || retry.InitialDelay < TimeSpan.Zero
            || retry.InitialDelay > TimeSpan.FromMinutes(10) || retry.BackoffMultiplier is < 1 or > 10)
        {
            errors.Add("retry must have 0-20 attempts, initial delay 0-600 s and multiplier 1-10");
        }

        foreach (var secret in stage.RequiredSecrets)
        {
            if (!SecretPattern().IsMatch(secret))
            {
                errors.Add($"invalid secret name '{secret}' (use UPPER_SNAKE_CASE)");
            }
        }

        if (stage.RequiredSecrets.Distinct(StringComparer.Ordinal).Count() != stage.RequiredSecrets.Count)
        {
            errors.Add("secret names must be unique");
        }

        ValidateFields(stage.Inputs, "inputs", errors);
        ValidateFields(stage.Outputs, "outputs", errors);
        return errors;
    }

    /// <exception cref="PsrConfigurationException">The definition is invalid.</exception>
    public static void EnsureValid(StageDefinition stage, string source)
    {
        var errors = Validate(stage);
        if (errors.Count > 0)
        {
            throw new PsrConfigurationException($"Invalid stage '{stage.Key}@{stage.Version}' ({source}): {string.Join("; ", errors)}.");
        }
    }

    internal static void ValidateFields(IReadOnlyList<FieldDefinition> fields, string section, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var where = $"{section}.{field.Name}";
            if (!FieldNamePattern().IsMatch(field.Name))
            {
                errors.Add($"{where}: name must match ^[a-z_][a-z0-9_]{{0,63}}$");
            }

            if (!names.Add(field.Name))
            {
                errors.Add($"{where}: duplicate field name");
            }

            if (field.Description is { Length: > 500 })
            {
                errors.Add($"{where}: description is longer than 500 chars");
            }

            var type = field.Type;
            if (field.ContentTypes is not null && !type.IsFile)
            {
                errors.Add($"{where}: contentTypes is only allowed on file fields");
            }

            if (field.Columns is { } columns)
            {
                if (type.Kind != FieldKind.Table)
                {
                    errors.Add($"{where}: columns is only allowed on table fields");
                }
                else if (columns.Count == 0 || columns.Any(c => !c.Type.IsScalar || string.IsNullOrEmpty(c.Name)))
                {
                    errors.Add($"{where}: columns need a name and a scalar type");
                }
            }

            if (field.Default is { } value)
            {
                if (type.IsFile || type.Kind == FieldKind.Table)
                {
                    errors.Add($"{where}: {type.WireName} fields cannot have a default");
                    continue;
                }

                var wireErrors = new List<WireError>();
                WireValidator.Validate(value, field, $"{where}.default", wireErrors);
                errors.AddRange(wireErrors.Select(e => $"{e.Path}: {e.Message}"));
            }
        }
    }
}
