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
}
