using System.IO.Compression;
using System.Text.Json;
using ReactorSoftInterlock.Application.G2000;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;
using ReactorSoftInterlock.Infrastructure.Logging;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class ExperimentSessionRecorderTests
{
    [Fact]
    public async Task CreateAsync_CreatesSessionFilesAndInitialSettingsSnapshot()
    {
        var root = CreateTemporaryDirectory();
        var settings = new AppSettings
        {
            ThresholdC = 60.0,
            WindowTitleContains = "Hikmicro Analyzer"
        };

        await using var recorder = await ExperimentSessionRecorder.CreateAsync(
            root,
            settings,
            "2026.07.30-test",
            CancellationToken.None);

        Assert.True(Directory.Exists(recorder.SessionDirectory));
        Assert.True(File.Exists(Path.Combine(recorder.SessionDirectory, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(recorder.SessionDirectory, "settings-start.json")));
        Assert.True(File.Exists(Path.Combine(recorder.SessionDirectory, "settings-latest.json")));
        Assert.True(File.Exists(Path.Combine(recorder.SessionDirectory, "temperature-samples.csv")));
        Assert.True(File.Exists(Path.Combine(recorder.SessionDirectory, "events.csv")));
        Assert.True(File.Exists(Path.Combine(recorder.SessionDirectory, "g2000-telemetry.csv")));
        Assert.True(File.Exists(Path.Combine(recorder.SessionDirectory, "gas-flow.csv")));
        Assert.True(File.Exists(Path.Combine(recorder.SessionDirectory, "lab-profile.json")));
        Assert.True(File.Exists(Path.Combine(recorder.SessionDirectory, "experiment-context.md")));
        Assert.False(File.Exists(Path.Combine(recorder.SessionDirectory, "manual-fields.md")));

        var settingsJson = await File.ReadAllTextAsync(
            Path.Combine(recorder.SessionDirectory, "settings-start.json"));
        Assert.Contains("\"ThresholdC\": 60", settingsJson);
        Assert.Contains("Hikmicro Analyzer", settingsJson);

        var experimentContext = await File.ReadAllTextAsync(
            Path.Combine(recorder.SessionDirectory, "experiment-context.md"));
        Assert.Contains("HIKMICRO E20Plus", experimentContext, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not_recorded", experimentContext, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordsEvidenceAndBuildsSummaryWithoutLosingLastQueuedItem()
    {
        var root = CreateTemporaryDirectory();
        var exportPath = Path.Combine(root, "experiment-bundle.zip");
        var settings = new AppSettings { ThresholdC = 60.0 };
        await using var recorder = await ExperimentSessionRecorder.CreateAsync(
            root,
            settings,
            "2026.07.30-test",
            CancellationToken.None);

        recorder.RecordTemperature(CreateSample(
            "2026-07-30T10:00:00Z",
            55.0,
            "Max: 55.0 C",
            MonitorStatus.Monitoring,
            RelayAction.None));
        recorder.RecordTemperature(CreateSample(
            "2026-07-30T10:00:01Z",
            null,
            "No, \"reading\"\nsecond line",
            MonitorStatus.NoReading,
            RelayAction.None));
        recorder.RecordTemperature(CreateSample(
            "2026-07-30T10:00:02Z",
            160.0,
            "Max: 160.0 C",
            MonitorStatus.Tripped,
            RelayAction.StopSent));
        recorder.RecordTemperature(CreateSample(
            "2026-07-30T10:00:03Z",
            76.5,
            "Max: 76.5 C",
            MonitorStatus.Monitoring,
            RelayAction.ResetSent));
        recorder.RecordEvent(
            "relay",
            "open-all-interlocks",
            "command-completed",
            "Engineering, Test; hardware_feedback=false");
        recorder.RecordEvent(
            "relay",
            "close-all-interlocks",
            "command-completed",
            "Engineering, Restore; hardware_feedback=false");
        recorder.RecordEvent(
            "interlock",
            "manual-reset",
            "command-completed",
            "hardware_feedback=false");
        recorder.RecordEvent(
            "g2000",
            "set-hv-on",
            "failed",
            "PCAN unavailable");
        recorder.RecordEvent(
            "g2000",
            "set-hv-ready",
            "blocked",
            "Writable setpoints have not been applied.");
        recorder.RecordEvent("monitoring", "poll-loop", "failed", "OCR process failed");
        recorder.RecordG2000Telemetry(new G2000TelemetrySnapshot
        {
            Connected = true,
            CommunicationHealthy = true,
            LastReceivedAt = DateTimeOffset.Parse("2026-07-30T10:00:02Z"),
            Ready = false,
            Fault = true,
            HvEnable = false,
            HvOn = false,
            Source = G2000ControlSource.CanBus,
            ErrorCode = 0x08,
            ErrorText = "Interlock",
            DcLinkVoltageV = 43.0,
            FrequencyKhz = 110.0,
            DutyPercent = 45.0,
            StatusFrameHex = "188 08 00 03 08 00 00 00 00",
            TargetHvState = G2000HvState.HvAus,
            TripLatched = true,
            TripReason = "Temperature threshold",
            TargetSetpoints = new G2000WritableSetpoints { VoltageV = 43.0 },
            ActualSetpoints = new G2000WritableSetpoints { VoltageV = 42.8 }
        });
        recorder.RecordGasFlow(1.25, true, "success", "periodic read");

        settings.ThresholdC = 195.0;
        recorder.UpdateSettings(settings);
        recorder.RecordEvent("application", "last-before-export", "success", "must be in ZIP");

        await recorder.ExportAsync(exportPath, CancellationToken.None);

        var temperatureCsv = await File.ReadAllTextAsync(
            Path.Combine(recorder.SessionDirectory, "temperature-samples.csv"));
        Assert.Contains("\"No, \"\"reading\"\"\nsecond line\"", temperatureCsv);

        var latestSettings = await File.ReadAllTextAsync(
            Path.Combine(recorder.SessionDirectory, "settings-latest.json"));
        Assert.Contains("\"ThresholdC\": 195", latestSettings);

        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(recorder.SessionDirectory, "report-summary.json")));
        var rootElement = summary.RootElement;
        Assert.Equal(4, rootElement.GetProperty("TemperatureSampleCount").GetInt32());
        Assert.Equal(3, rootElement.GetProperty("ValidTemperatureCount").GetInt32());
        Assert.Equal(0.75, rootElement.GetProperty("ValidTemperatureRate").GetDouble());
        Assert.Equal(1, rootElement.GetProperty("NoReadingCount").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("AutomaticTemperatureTripCount").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("AutomaticRecoveryCount").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("EngineeringOpenAllCommandCount").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("EngineeringCloseAllCommandCount").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("ManualResetCommandCount").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("FailedControlCommandCount").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("BlockedControlCommandCount").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("MonitoringLoopFailureCount").GetInt32());
        Assert.Equal(55.0, rootElement.GetProperty("MinimumTemperatureC").GetDouble());
        Assert.Equal(160.0, rootElement.GetProperty("MaximumTemperatureC").GetDouble());
        Assert.Equal(1, rootElement.GetProperty("G2000TelemetryCount").GetInt32());
        Assert.Equal(1, rootElement.GetProperty("GasFlowSampleCount").GetInt32());

        var gasFlowCsv = await File.ReadAllLinesAsync(
            Path.Combine(recorder.SessionDirectory, "gas-flow.csv"));
        Assert.Contains("actual_flow_ml_min", gasFlowCsv[0]);

        using var archive = ZipFile.OpenRead(exportPath);
        var names = archive.Entries.Select(entry => entry.FullName).ToHashSet();
        Assert.Contains("manifest.json", names);
        Assert.Contains("settings-start.json", names);
        Assert.Contains("settings-latest.json", names);
        Assert.Contains("temperature-samples.csv", names);
        Assert.Contains("events.csv", names);
        Assert.Contains("g2000-telemetry.csv", names);
        Assert.Contains("gas-flow.csv", names);
        Assert.Contains("report-summary.json", names);
        Assert.Contains("lab-profile.json", names);
        Assert.Contains("experiment-context.md", names);
        Assert.DoesNotContain("manual-fields.md", names);

        var eventEntry = archive.GetEntry("events.csv");
        Assert.NotNull(eventEntry);
        using var reader = new StreamReader(eventEntry!.Open());
        Assert.Contains("last-before-export", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task G2000Telemetry_IsRateLimitedButRecordsSignificantStateChangesImmediately()
    {
        var root = CreateTemporaryDirectory();
        await using var recorder = await ExperimentSessionRecorder.CreateAsync(
            root,
            new AppSettings(),
            "2026.07.30-test",
            CancellationToken.None);
        var snapshot = new G2000TelemetrySnapshot
        {
            Connected = true,
            CommunicationHealthy = true,
            Ready = true,
            Source = G2000ControlSource.CanBus
        };

        for (var index = 0; index < 100; index++)
        {
            snapshot.DcLinkVoltageV = 42.0 + index / 100.0;
            recorder.RecordG2000Telemetry(snapshot);
        }

        snapshot.Fault = true;
        snapshot.ErrorCode = 0x08;
        snapshot.ErrorText = "Interlock";
        recorder.RecordG2000Telemetry(snapshot);
        await recorder.FlushAsync(CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(
            Path.Combine(recorder.SessionDirectory, "g2000-telemetry.csv"));
        Assert.Equal(3, lines.Length);
        Assert.Contains("0x08", lines[2]);

        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        recorder.RecordG2000Telemetry(snapshot);
        await recorder.FlushAsync(CancellationToken.None);

        lines = await File.ReadAllLinesAsync(
            Path.Combine(recorder.SessionDirectory, "g2000-telemetry.csv"));
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public async Task ExportAsync_RejectsSessionSourceAsDestinationWithoutCorruptingIt()
    {
        var root = CreateTemporaryDirectory();
        await using var recorder = await ExperimentSessionRecorder.CreateAsync(
            root,
            new AppSettings(),
            "2026.07.30-test",
            CancellationToken.None);
        var manifestPath = Path.Combine(recorder.SessionDirectory, "manifest.json");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => recorder.ExportAsync(manifestPath, CancellationToken.None));

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        Assert.Equal(recorder.SessionId, manifest.RootElement.GetProperty("SessionId").GetString());
    }

    [Fact]
    public async Task ExportAsync_FailurePreservesExistingBundle()
    {
        var root = CreateTemporaryDirectory();
        var exportPath = Path.Combine(root, "existing.zip");
        var originalContent = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(exportPath, originalContent);
        await using var recorder = await ExperimentSessionRecorder.CreateAsync(
            root,
            new AppSettings(),
            "2026.07.30-test",
            CancellationToken.None);
        File.Delete(Path.Combine(recorder.SessionDirectory, "lab-profile.json"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => recorder.ExportAsync(exportPath, CancellationToken.None));

        Assert.Equal(originalContent, await File.ReadAllBytesAsync(exportPath));
        Assert.Empty(Directory.GetFiles(root, ".existing.zip.*.tmp"));
    }

    [Fact]
    public async Task RecordEvent_WriteFailureDoesNotEscapeAndIsCounted()
    {
        var root = CreateTemporaryDirectory();
        await using var recorder = await ExperimentSessionRecorder.CreateAsync(
            root,
            new AppSettings(),
            "2026.07.30-test",
            CancellationToken.None);
        await recorder.FlushAsync(CancellationToken.None);
        var eventsPath = Path.Combine(recorder.SessionDirectory, "events.csv");
        File.Delete(eventsPath);
        Directory.CreateDirectory(eventsPath);

        var exception = Record.Exception(
            () => recorder.RecordEvent("relay", "open-all-interlocks", "failed", "test"));
        await recorder.FlushAsync(CancellationToken.None);

        Assert.Null(exception);
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(recorder.SessionDirectory, "report-summary.json")));
        Assert.Equal(
            1,
            summary.RootElement.GetProperty("RecordingFailureCount").GetInt32());
    }

    [Fact]
    public async Task RelayFailureObserver_CountsOnlyRelayFailureAsControlFailure()
    {
        var root = CreateTemporaryDirectory();
        await using var recorder = await ExperimentSessionRecorder.CreateAsync(
            root,
            new AppSettings(),
            "2026.07.30-test",
            CancellationToken.None);
        var observer = new ExperimentRelayFailureObserver(
            new ThrowingRelayController(),
            (action, outcome, details) =>
                recorder.RecordEvent("interlock", action, outcome, details));

        recorder.RecordEvent("monitoring", "poll-loop", "failed", "OCR process failed");
        var exception = await Assert.ThrowsAsync<IOException>(
            () => observer.StopAsync(CancellationToken.None));
        await recorder.FlushAsync(CancellationToken.None);

        Assert.Equal("Relay write failed.", exception.Message);
        using var summary = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(recorder.SessionDirectory, "report-summary.json")));
        Assert.Equal(
            1,
            summary.RootElement.GetProperty("FailedControlCommandCount").GetInt32());
        Assert.Equal(
            1,
            summary.RootElement.GetProperty("MonitoringLoopFailureCount").GetInt32());

        var events = await File.ReadAllTextAsync(
            Path.Combine(recorder.SessionDirectory, "events.csv"));
        Assert.Contains("automatic-stop", events);
        Assert.Contains("poll-loop", events);
    }

    [Fact]
    public async Task ExportAsync_CreatesAConsistentSnapshotWhileNewRecordsQueueBehindIt()
    {
        var root = CreateTemporaryDirectory();
        var exportPath = Path.Combine(root, "consistent.zip");
        await using var recorder = await ExperimentSessionRecorder.CreateAsync(
            root,
            new AppSettings(),
            "2026.07.30-test",
            CancellationToken.None);
        recorder.RecordEvent("test", "before-export", "success");

        var exportTask = recorder.ExportAsync(exportPath, CancellationToken.None);
        for (var index = 0; index < 100; index++)
        {
            recorder.RecordEvent("test", $"after-export-{index}", "success");
        }

        await exportTask;

        using var archive = ZipFile.OpenRead(exportPath);
        var eventsEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("events.csv"));
        using var eventReader = new StreamReader(eventsEntry.Open());
        var eventsText = await eventReader.ReadToEndAsync();
        Assert.Contains("before-export", eventsText);
        Assert.DoesNotContain("after-export-", eventsText);

        var summaryEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("report-summary.json"));
        using var summaryReader = new StreamReader(summaryEntry.Open());
        using var summary = JsonDocument.Parse(await summaryReader.ReadToEndAsync());
        var eventLineCount = eventsText.Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries).Length - 1;
        Assert.Equal(
            eventLineCount,
            summary.RootElement.GetProperty("EventCount").GetInt32());
    }

    [Fact]
    public async Task RecordMethods_AfterDisposal_DoNotThrowIntoControlPath()
    {
        var root = CreateTemporaryDirectory();
        var recorder = await ExperimentSessionRecorder.CreateAsync(
            root,
            new AppSettings(),
            "2026.07.30-test",
            CancellationToken.None);
        await recorder.DisposeAsync();

        using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                   Path.Combine(recorder.SessionDirectory, "manifest.json"))))
        {
            Assert.Equal(
                JsonValueKind.String,
                manifest.RootElement.GetProperty("EndedAtUtc").ValueKind);
        }

        var exception = Record.Exception(() =>
        {
            recorder.RecordTemperature(CreateSample(
                "2026-07-30T10:00:00Z",
                90.0,
                "Max: 90.0 C",
                MonitorStatus.Tripped,
                RelayAction.StopSent));
            recorder.RecordEvent("relay", "open-all", "success");
            recorder.RecordG2000Telemetry(new G2000TelemetrySnapshot());
            recorder.RecordGasFlow(null, true, "error", "device unavailable");
            recorder.UpdateSettings(new AppSettings());
        });

        Assert.Null(exception);
    }

    private static TemperatureSample CreateSample(
        string timestamp,
        double? temperatureC,
        string rawOcr,
        MonitorStatus status,
        RelayAction relayAction)
    {
        return new TemperatureSample(
            DateTimeOffset.Parse(timestamp),
            temperatureC,
            rawOcr,
            status,
            status == MonitorStatus.Tripped ? "Temperature reached threshold." : string.Empty,
            relayAction,
            "Hikmicro:1,2,3,4");
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class ThrowingRelayController : IRelayController
    {
        public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
        {
            return Task.FromException<RelayAction>(new IOException("Relay write failed."));
        }

        public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(RelayAction.ResetSent);
        }

        public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(RelayAction.TestStopSent);
        }
    }
}
