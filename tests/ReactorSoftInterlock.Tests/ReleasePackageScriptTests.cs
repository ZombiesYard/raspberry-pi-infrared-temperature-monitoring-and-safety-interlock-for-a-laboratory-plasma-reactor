using System.Diagnostics;

namespace ReactorSoftInterlock.Tests;

public sealed class ReleasePackageScriptTests
{
    [Fact]
    public async Task ScriptResolvesWindowsDotNetExecutableWithoutRelyingOnPathext()
    {
        var result = await RunScriptAsync("-ResolveDotNetOnly");

        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith("dotnet.exe", result.Output.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidatorAcceptsOnlyKnownSoftwareArtifacts()
    {
        var directory = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory, "ReactorSoftInterlock.Wpf.exe"), "placeholder");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "appsettings.json"), "{\"DataDirectory\":\"data\"}");

        var result = await RunValidatorAsync(directory);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("release-directory-valid", result.Output);
    }

    [Theory]
    [InlineData("events.csv", "timestamp,category")]
    [InlineData("report-summary.json", "{}")]
    [InlineData("gitlab-pat.txt", "secret-value")]
    [InlineData(".env", "PRIVATE_TOKEN=secret-value")]
    [InlineData("ReactorSoftInterlock.Wpf.pdb", "C:\\Users\\operator\\private-build-path")]
    public async Task ValidatorRejectsFlatEvidenceAndCredentialFiles(string fileName, string content)
    {
        var directory = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory, "ReactorSoftInterlock.Wpf.exe"), "placeholder");
        await File.WriteAllTextAsync(Path.Combine(directory, fileName), content);

        var result = await RunValidatorAsync(directory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("outside the software allowlist", result.Output);
    }

    [Fact]
    public async Task ValidatorRejectsCredentialLikeAppSettings()
    {
        var directory = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory, "ReactorSoftInterlock.Wpf.exe"), "placeholder");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "appsettings.json"), "{\"PrivateToken\":\"secret-value\"}");

        var result = await RunValidatorAsync(directory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("credential-like field", result.Output);
        Assert.DoesNotContain("secret-value", result.Output);
    }

    [Fact]
    public async Task ValidatorRejectsEvidenceDirectoryEvenWhenFileExtensionLooksBinary()
    {
        var directory = CreateTemporaryDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(directory, "ReactorSoftInterlock.Wpf.exe"), "placeholder");
        var dataDirectory = Path.Combine(directory, "data", "experiment-bundles");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "renamed-evidence.dll"), "evidence");

        var result = await RunValidatorAsync(directory);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("renamed-evidence.dll", result.Output);
    }

    private static async Task<(int ExitCode, string Output)> RunValidatorAsync(string directory)
        => await RunScriptAsync("-ValidateOnlyDirectory", directory);

    private static async Task<(int ExitCode, string Output)> RunScriptAsync(params string[] arguments)
    {
        var repositoryRoot = FindRepositoryRoot();
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "scripts", "Package-Release.ps1"));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, $"{await standardOutput}\n{await standardError}");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "Package-Release.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
