namespace ReactorSoftInterlock.Infrastructure;

public static class RuntimePathResolver
{
    public static string ResolveExecutablePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        if (Path.IsPathFullyQualified(configuredPath))
        {
            return configuredPath;
        }

        if (configuredPath.Contains(Path.DirectorySeparatorChar) || configuredPath.Contains(Path.AltDirectorySeparatorChar))
        {
            return Path.Combine(AppContext.BaseDirectory, configuredPath);
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, configuredPath);
        return File.Exists(bundled) ? bundled : configuredPath;
    }

    public static bool ExecutableExists(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        var resolved = ResolveExecutablePath(configuredPath);
        if (File.Exists(resolved))
        {
            return true;
        }

        if (resolved != configuredPath)
        {
            return false;
        }

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return paths.Any(path => File.Exists(Path.Combine(path, configuredPath)));
    }
}
