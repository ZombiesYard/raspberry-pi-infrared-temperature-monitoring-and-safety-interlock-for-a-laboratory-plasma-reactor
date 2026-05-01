using ReactorSoftInterlock.Infrastructure;

namespace ReactorSoftInterlock.Tests;

public sealed class RuntimePathResolverTests
{
    [Fact]
    public void FindsExecutableOnPath()
    {
        var tempDirectory = CreateTempDirectory();
        var executablePath = Path.Combine(tempDirectory, "demo-tool.exe");
        File.WriteAllText(executablePath, string.Empty);

        WithEnvironmentVariable("PATH", tempDirectory, () =>
        {
            Assert.True(RuntimePathResolver.ExecutableExists("demo-tool.exe"));
            Assert.Equal(executablePath, RuntimePathResolver.ResolveExecutablePath("demo-tool.exe"));
        });
    }

    [Fact]
    public void FindsTesseractInCommonProgramFilesLocation()
    {
        var programFiles = CreateTempDirectory();
        var tesseractDirectory = Path.Combine(programFiles, "Tesseract-OCR");
        Directory.CreateDirectory(tesseractDirectory);

        var executablePath = Path.Combine(tesseractDirectory, "tesseract.exe");
        File.WriteAllText(executablePath, string.Empty);

        WithEnvironmentVariable("ProgramFiles", programFiles, () =>
        WithEnvironmentVariable("ProgramFiles(x86)", string.Empty, () =>
        WithEnvironmentVariable("PATH", string.Empty, () =>
        {
            Assert.True(RuntimePathResolver.ExecutableExists(@"offline-deps\tesseract\tesseract.exe"));
            Assert.Equal(executablePath, RuntimePathResolver.ResolveExecutablePath(@"offline-deps\tesseract\tesseract.exe"));
        })));
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WithEnvironmentVariable(string name, string? value, Action action)
    {
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);

        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }
}
