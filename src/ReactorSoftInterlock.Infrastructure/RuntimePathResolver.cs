namespace ReactorSoftInterlock.Infrastructure;

public static class RuntimePathResolver
{
    public static string ResolveExecutablePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return GetCandidatePaths(configuredPath).FirstOrDefault(File.Exists) ?? GetDefaultResolvedPath(configuredPath);
    }

    public static bool ExecutableExists(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        return GetCandidatePaths(configuredPath).Any(File.Exists);
    }

    private static IEnumerable<string> GetCandidatePaths(string configuredPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                seen.Add(path);
            }
        }

        Add(GetDefaultResolvedPath(configuredPath));

        var fileName = Path.GetFileName(configuredPath);
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                         .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(Path.Combine(path, fileName));
            }
        }

        if (IsTesseractExecutable(configuredPath))
        {
            foreach (var candidate in GetCommonTesseractLocations())
            {
                Add(candidate);
            }
        }

        return seen;
    }

    private static string GetDefaultResolvedPath(string configuredPath)
    {
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

    private static bool IsTesseractExecutable(string configuredPath)
    {
        return string.Equals(Path.GetFileName(configuredPath), "tesseract.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetCommonTesseractLocations()
    {
        foreach (var baseDirectory in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                     @"C:\Tools"
                 })
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                continue;
            }

            yield return Path.Combine(baseDirectory, "Tesseract-OCR", "tesseract.exe");
        }
    }
}
