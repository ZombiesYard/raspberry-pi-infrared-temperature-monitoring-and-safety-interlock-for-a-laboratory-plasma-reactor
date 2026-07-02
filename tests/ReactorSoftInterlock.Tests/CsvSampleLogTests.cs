using ReactorSoftInterlock.Domain;
using ReactorSoftInterlock.Infrastructure.Logging;

namespace ReactorSoftInterlock.Tests;

public sealed class CsvSampleLogTests
{
    [Fact]
    public async Task WritesHeaderAndSample()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.csv");
        var log = new CsvSampleLog(path);
        var sample = new TemperatureSample(
            DateTimeOffset.Parse("2026-04-14T12:00:00+02:00"),
            90.1,
            "Max 90.1 C",
            MonitorStatus.Tripped,
            "Temperature 90.1 C reached or exceeded 90.0 C.",
            RelayAction.StopSent,
            "Hikmicro:1,2,3,4");

        await log.AppendAsync(sample, CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(CsvSampleLog.Header, lines[0]);
        Assert.Contains("90.1", lines[1]);
        Assert.Contains("Tripped", lines[1]);
        Assert.Contains("StopSent", lines[1]);
    }

    [Fact]
    public async Task ClearAsync_RemovesSamplesButKeepsExportableHeader()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.csv");
        var exportPath = Path.Combine(directory, "export.csv");
        var log = new CsvSampleLog(path);
        var sample = new TemperatureSample(
            DateTimeOffset.Parse("2026-04-14T12:00:00+02:00"),
            90.1,
            "Max 90.1 C",
            MonitorStatus.Tripped,
            "Temperature 90.1 C reached or exceeded 90.0 C.",
            RelayAction.StopSent,
            "Hikmicro:1,2,3,4");

        await log.AppendAsync(sample, CancellationToken.None);
        await log.ClearAsync(CancellationToken.None);

        Assert.Empty(await log.ReadRecentAsync(200, CancellationToken.None));
        await log.ExportAsync(exportPath, CancellationToken.None);

        Assert.Equal(new[] { CsvSampleLog.Header }, await File.ReadAllLinesAsync(exportPath));
    }

    [Fact]
    public async Task ExportAsync_AfterClearStartsFromNewSamples()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.csv");
        var exportPath = Path.Combine(directory, "export.csv");
        var log = new CsvSampleLog(path);

        await log.AppendAsync(CreateSample(1), CancellationToken.None);
        await log.ClearAsync(CancellationToken.None);
        await log.AppendAsync(CreateSample(2), CancellationToken.None);
        await log.ExportAsync(exportPath, CancellationToken.None);

        var exported = await File.ReadAllTextAsync(exportPath);
        Assert.DoesNotContain("91.1", exported);
        Assert.Contains("92.1", exported);
    }

    [Fact]
    public async Task ClearAsync_IsSerializedAcrossInstancesForSamePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.csv");
        var exportPath = Path.Combine(directory, "export.csv");
        var monitoringLog = new CsvSampleLog(path);
        var uiLog = new CsvSampleLog(path);

        await monitoringLog.AppendAsync(CreateSample(1), CancellationToken.None);
        await uiLog.ClearAsync(CancellationToken.None);
        await monitoringLog.AppendAsync(CreateSample(2), CancellationToken.None);
        await uiLog.ExportAsync(exportPath, CancellationToken.None);

        var exported = await File.ReadAllTextAsync(exportPath);
        Assert.DoesNotContain("91.1", exported);
        Assert.Contains("92.1", exported);
    }

    [Fact]
    public async Task ClearAsync_IsSerializedWithAppendAndExport()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "history.csv");
        var exportPath = Path.Combine(directory, "export.csv");
        var log = new CsvSampleLog(path);

        var appendTasks = Enumerable.Range(0, 20)
            .Select(index => log.AppendAsync(CreateSample(index), CancellationToken.None));
        var tasks = appendTasks
            .Concat(new[]
            {
                log.ClearAsync(CancellationToken.None),
                log.ExportAsync(exportPath, CancellationToken.None)
            });

        await Task.WhenAll(tasks);

        var exported = await File.ReadAllLinesAsync(exportPath);
        Assert.NotEmpty(exported);
        Assert.Equal(CsvSampleLog.Header, exported[0]);
    }

    private static TemperatureSample CreateSample(int index)
    {
        return new TemperatureSample(
            DateTimeOffset.Parse("2026-04-14T12:00:00+02:00").AddSeconds(index),
            90.1 + index,
            $"Max {90.1 + index:0.0} C",
            MonitorStatus.Tripped,
            "Temperature reached threshold.",
            RelayAction.StopSent,
            "Hikmicro:1,2,3,4");
    }
}
