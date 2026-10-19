using Microsoft.Extensions.Options;
using PythonScriptRunner.Core.Catalog;
using PythonScriptRunner.Core.Environments;

namespace PythonScriptRunner.Core;

/// <summary>Validates <see cref="PythonScriptRunnerOptions"/>, including that uv can be found.</summary>
internal sealed class PythonScriptRunnerOptionsValidator : IValidateOptions<PythonScriptRunnerOptions>
{
    public ValidateOptionsResult Validate(string? name, PythonScriptRunnerOptions options)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(options.WorkRoot))
        {
            errors.Add("WorkRoot is required.");
        }

        if (string.IsNullOrWhiteSpace(options.EnvironmentCacheRoot))
        {
            errors.Add("EnvironmentCacheRoot is required.");
        }

        if (!StageDefinitionValidator.PythonVersionPattern().IsMatch(options.DefaultPythonVersion ?? string.Empty))
        {
            errors.Add($"DefaultPythonVersion '{options.DefaultPythonVersion}' must look like 3.12 (minimum 3.10).");
        }

        if (options.DefaultTimeout <= TimeSpan.Zero)
        {
            errors.Add("DefaultTimeout must be positive.");
        }

        if (options.DefaultMemoryLimitMb < 64)
        {
            errors.Add("DefaultMemoryLimitMb must be at least 64.");
        }

        if (options.MaxFileBytes <= 0 || options.MaxRunFilesBytes < options.MaxFileBytes)
        {
            errors.Add("MaxFileBytes must be positive and MaxRunFilesBytes must be at least MaxFileBytes.");
        }

        if (options.MaxLogBytes < 1024)
        {
            errors.Add("MaxLogBytes must be at least 1024.");
        }

        if (options.MaxConcurrentProcesses < 1)
        {
            errors.Add("MaxConcurrentProcesses must be at least 1.");
        }

        if (string.IsNullOrWhiteSpace(options.PsrunnerRequirement))
        {
            errors.Add("PsrunnerRequirement is required.");
        }

        if (ExecutableLocator.Find(options.UvPath) is null)
        {
            errors.Add($"The uv executable '{options.UvPath}' was not found. Install uv (https://docs.astral.sh/uv/) or set UvPath.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
