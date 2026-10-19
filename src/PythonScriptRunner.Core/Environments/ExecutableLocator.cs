namespace PythonScriptRunner.Core.Environments;

/// <summary>Finds an executable by path or in the PATH.</summary>
internal static class ExecutableLocator
{
    public static string? Find(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath))
        {
            return null;
        }

        if (Path.IsPathRooted(nameOrPath) || nameOrPath.Contains(Path.DirectorySeparatorChar) || nameOrPath.Contains(Path.AltDirectorySeparatorChar))
        {
            var full = Path.GetFullPath(nameOrPath);
            return Candidates(full).FirstOrDefault(File.Exists);
        }

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return directories
            .SelectMany(directory => Candidates(Path.Combine(directory, nameOrPath)))
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> Candidates(string path)
    {
        yield return path;
        if (OperatingSystem.IsWindows() && !Path.HasExtension(path))
        {
            yield return path + ".exe";
            yield return path + ".cmd";
        }
    }
}
