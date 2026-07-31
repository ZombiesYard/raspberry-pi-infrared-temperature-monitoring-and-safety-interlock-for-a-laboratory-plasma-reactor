using System.IO.Compression;
using ReactorSoftInterlock.Domain;
using ReactorSoftInterlock.Infrastructure.Logging;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class ExperimentEvidenceCoordinatorTests
{
    [Fact]
    public void RetryScheduleUsesThirtySecondsTwoMinutesTenMinutesThenThirtyMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ExperimentEvidenceCoordinator.GetRetryDelay(1));
        Assert.Equal(TimeSpan.FromMinutes(2), ExperimentEvidenceCoordinator.GetRetryDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(10), ExperimentEvidenceCoordinator.GetRetryDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(30), ExperimentEvidenceCoordinator.GetRetryDelay(4));
        Assert.Equal(TimeSpan.FromMinutes(30), ExperimentEvidenceCoordinator.GetRetryDelay(20));
    }

    [Fact]
    public async Task MultipleRunFinalizations_CreateIndependentShortBundlesWithoutManualExport()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: false);
        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", uploader: null, CancellationToken.None);

        var firstSession = coordinator.ActiveSessionId;
        coordinator.RecordEvent("monitoring", "started", "success");
        coordinator.RecordTemperature(CreateSample(71.0, "first-run-marker"));
        var first = await coordinator.FinalizeRunAsync(
            settings, "monitoring-stopped", startNextRun: true, CancellationToken.None);

        var secondSession = coordinator.ActiveSessionId;
        coordinator.RecordEvent("monitoring", "started", "success");
        coordinator.RecordTemperature(CreateSample(82.0, "second-run-marker"));
        var second = await coordinator.FinalizeRunAsync(
            settings, "monitoring-stopped", startNextRun: true, CancellationToken.None);

        Assert.True(first.Created);
        Assert.True(second.Created);
        Assert.NotEqual(firstSession, secondSession);
        Assert.Matches("^exp-[0-9]{8}-[0-9]{6}-[a-f0-9]{8}\\.zip$", Path.GetFileName(first.BundlePath));
        Assert.True(Path.GetFileName(first.BundlePath).Length < 40);
        Assert.True(File.Exists(first.BundlePath));
        Assert.True(File.Exists(second.BundlePath));
        AssertBundleContains(first.BundlePath, "first-run-marker", "second-run-marker");
        AssertBundleContains(second.BundlePath, "second-run-marker", "first-run-marker");
    }

    [Fact]
    public async Task EmptyRunIsSkippedButEngineeringOnlyRunIsPackagedAtShutdown()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: false);
        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", uploader: null, CancellationToken.None);

        var empty = await coordinator.FinalizeRunAsync(
            settings, "application-exit", startNextRun: true, CancellationToken.None);
        Assert.False(empty.Created);

        coordinator.RecordEvent("relay", "open-all-interlocks", "command-completed", "engineering-only");
        var engineering = await coordinator.FinalizeRunAsync(
            settings, "application-exit", startNextRun: false, CancellationToken.None);

        Assert.True(engineering.Created);
        AssertBundleContains(engineering.BundlePath, "engineering-only", "not-present");
    }

    [Fact]
    public async Task RotationMovesNewEventsToNextRecorderBeforeOldRecorderPackages()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: false);
        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", uploader: null, CancellationToken.None);
        coordinator.RecordEvent("monitoring", "started", "success", "old-run");

        var result = await coordinator.FinalizeRunAsync(
            settings, "monitoring-stopped", startNextRun: true, CancellationToken.None);
        coordinator.RecordEvent("relay", "new-run-event", "success", "must-not-enter-old-run");
        await coordinator.FlushAsync(CancellationToken.None);

        AssertBundleContains(result.BundlePath, "old-run", "must-not-enter-old-run");
        var currentEvents = await File.ReadAllTextAsync(
            Path.Combine(coordinator.ActiveSessionDirectory, "events.csv"));
        Assert.Contains("must-not-enter-old-run", currentEvents);
    }

    [Fact]
    public async Task EventsArrivingDuringRecorderCreationAreBufferedIntoNextRun()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: false);
        var secondCreationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecondCreation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var creationCount = 0;
        async Task<ExperimentSessionRecorder> Factory(
            string directory,
            AppSettings snapshot,
            string version,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref creationCount) == 2)
            {
                secondCreationEntered.SetResult();
                await allowSecondCreation.Task.WaitAsync(cancellationToken);
            }

            return await ExperimentSessionRecorder.CreateAsync(
                directory, snapshot, version, cancellationToken);
        }

        await using var coordinator = await ExperimentEvidenceCoordinator.CreateWithRecorderFactoryAsync(
            root, settings, "2026.07.31-test", uploader: null, Factory, CancellationToken.None);
        coordinator.RecordEvent("monitoring", "started", "success", "old-run");

        var finalization = coordinator.FinalizeRunAsync(
            settings, "monitoring-stopped", startNextRun: true, CancellationToken.None);
        await secondCreationEntered.Task;
        coordinator.RecordEvent("relay", "during-rotation", "success", "next-run-only");
        allowSecondCreation.SetResult();
        var result = await finalization;
        await coordinator.FlushAsync(CancellationToken.None);

        AssertBundleContains(result.BundlePath, "old-run", "next-run-only");
        var currentEvents = await File.ReadAllTextAsync(
            Path.Combine(coordinator.ActiveSessionDirectory, "events.csv"));
        Assert.Contains("next-run-only", currentEvents);
    }

    [Fact]
    public async Task BufferedEventKeepsItsOriginalCaptureTimestamp()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: false);
        var secondCreationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecondCreation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var creationCount = 0;
        async Task<ExperimentSessionRecorder> Factory(
            string directory,
            AppSettings snapshot,
            string version,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref creationCount) == 2)
            {
                secondCreationEntered.SetResult();
                await allowSecondCreation.Task.WaitAsync(cancellationToken);
            }

            return await ExperimentSessionRecorder.CreateAsync(
                directory, snapshot, version, cancellationToken);
        }

        await using var coordinator = await ExperimentEvidenceCoordinator.CreateWithRecorderFactoryAsync(
            root, settings, "2026.07.31-test", uploader: null, Factory, CancellationToken.None);
        coordinator.RecordEvent("monitoring", "started", "success");
        var finalization = coordinator.FinalizeRunAsync(
            settings, "monitoring-stopped", startNextRun: true, CancellationToken.None);
        await secondCreationEntered.Task;
        var capturedAt = DateTimeOffset.UtcNow;
        coordinator.RecordEvent("relay", "timestamp-marker", "success");
        await Task.Delay(250);
        var releasedAt = DateTimeOffset.UtcNow;
        allowSecondCreation.SetResult();
        await finalization;
        await coordinator.FlushAsync(CancellationToken.None);

        var row = (await File.ReadAllLinesAsync(
            Path.Combine(coordinator.ActiveSessionDirectory, "events.csv")))
            .Single(line => line.Contains("timestamp-marker", StringComparison.Ordinal));
        var recordedAt = DateTimeOffset.Parse(
            row.Split(',')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(recordedAt, capturedAt.AddMilliseconds(-100), capturedAt.AddMilliseconds(100));
        Assert.True(recordedAt < releasedAt.AddMilliseconds(-100));
    }

    [Fact]
    public async Task TransitionBufferIsBoundedAndCoalescesTelemetry()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: false);
        var secondCreationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSecondCreation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var creationCount = 0;
        async Task<ExperimentSessionRecorder> Factory(
            string directory,
            AppSettings snapshot,
            string version,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref creationCount) == 2)
            {
                secondCreationEntered.SetResult();
                await allowSecondCreation.Task.WaitAsync(cancellationToken);
            }

            return await ExperimentSessionRecorder.CreateAsync(
                directory, snapshot, version, cancellationToken);
        }

        await using var coordinator = await ExperimentEvidenceCoordinator.CreateWithRecorderFactoryAsync(
            root, settings, "2026.07.31-test", uploader: null, Factory, CancellationToken.None);
        coordinator.RecordEvent("monitoring", "started", "success");
        var finalization = coordinator.FinalizeRunAsync(
            settings, "monitoring-stopped", startNextRun: true, CancellationToken.None);
        await secondCreationEntered.Task;
        for (var index = 0; index < 2000; index++)
        {
            coordinator.RecordEvent("relay", $"buffered-{index}", "success");
            coordinator.RecordG2000Telemetry(new ReactorSoftInterlock.Application.G2000.G2000TelemetrySnapshot());
        }
        allowSecondCreation.SetResult();
        await finalization;
        await coordinator.FlushAsync(CancellationToken.None);

        var eventsText = await File.ReadAllTextAsync(
            Path.Combine(coordinator.ActiveSessionDirectory, "events.csv"));
        Assert.Contains("transition-buffer-dropped", eventsText);
        Assert.Contains("records=976", eventsText);
        Assert.Contains("telemetry=1999", eventsText);
        Assert.True((await File.ReadAllLinesAsync(
            Path.Combine(coordinator.ActiveSessionDirectory, "g2000-telemetry.csv"))).Length <= 2);
    }

    [Fact]
    public async Task FinalizedBundleCreatesPersistentPendingOutboxItem()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: true);
        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", new AlwaysOfflineUploader(), CancellationToken.None);
        coordinator.RecordEvent("monitoring", "started", "success");

        var result = await coordinator.FinalizeRunAsync(
            settings, "monitoring-stopped", startNextRun: true, CancellationToken.None);

        Assert.True(result.Created);
        await WaitUntilAsync(() => coordinator.UploadState == ExperimentUploadState.Failed);
        var pendingFiles = Directory.GetFiles(
            Path.Combine(root, "experiment-upload-outbox", "pending"), "*.json");
        Assert.Single(pendingFiles);
        var pending = await File.ReadAllTextAsync(pendingFiles[0]);
        Assert.Contains(Path.GetFileName(result.BundlePath), pending);
        Assert.DoesNotContain("secret-token", pending, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulBackgroundUploadWritesReceiptAndMovesOutboxItemToSent()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: true);
        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", new SuccessfulUploader(), CancellationToken.None);
        coordinator.RecordEvent("monitoring", "started", "success");

        var result = await coordinator.FinalizeRunAsync(
            settings, "monitoring-stopped", startNextRun: true, CancellationToken.None);
        var sentDirectory = Path.Combine(root, "experiment-upload-outbox", "sent");
        await WaitUntilAsync(() => Directory.GetFiles(sentDirectory, "*.json").Length == 1);

        Assert.Empty(Directory.GetFiles(
            Path.Combine(root, "experiment-upload-outbox", "pending"), "*.json"));
        Assert.True(File.Exists(Path.Combine(
            root, "experiment-sessions", result.SessionId, "upload-receipt.json")));
    }

    [Fact]
    public async Task CredentialBecomingAvailableRetriesMissingCredentialItemImmediately()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: true);
        var uploader = new MissingThenSuccessUploader();
        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", uploader, CancellationToken.None);
        coordinator.RecordEvent("monitoring", "started", "success");
        await coordinator.FinalizeRunAsync(
            settings, "monitoring-stopped", startNextRun: true, CancellationToken.None);
        await WaitUntilAsync(() => coordinator.UploadState == ExperimentUploadState.MissingCredential);

        coordinator.NotifyCredentialAvailability(true);
        await WaitUntilAsync(() => Directory.GetFiles(
            Path.Combine(root, "experiment-upload-outbox", "sent"), "*.json").Length == 1);

        Assert.Equal(2, uploader.AttemptCount);
    }

    [Fact]
    public async Task StartupRecoversRecordedSessionWithoutBundleCheckpointAndQueuesIt()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: true);
        string abandonedSession;
        await using (var recorder = await ExperimentSessionRecorder.CreateAsync(
                         root, settings, "2026.07.31-test", CancellationToken.None))
        {
            abandonedSession = recorder.SessionId;
            recorder.RecordEvent("relay", "open-all-interlocks", "command-completed", "recover-me");
            await recorder.FlushAsync(CancellationToken.None);
        }

        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", uploader: null, CancellationToken.None);

        var checkpointPath = Path.Combine(
            root, "experiment-sessions", abandonedSession, "bundle-checkpoint.json");
        Assert.True(File.Exists(checkpointPath));
        var checkpoint = await File.ReadAllTextAsync(checkpointPath);
        Assert.Contains("RecoveredAfterUncleanExit", checkpoint);
        Assert.Contains("true", checkpoint, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(
            root, "experiment-upload-outbox", "pending", $"{abandonedSession}.json")));
    }

    [Fact]
    public async Task HardCrashRecoveryRebuildsSummaryFromPersistedCsvFiles()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: false);
        var recorder = await ExperimentSessionRecorder.CreateAsync(
            root, settings, "2026.07.31-test", CancellationToken.None);
        try
        {
            recorder.RecordTemperature(new TemperatureSample(
                DateTimeOffset.UtcNow,
                71.5,
                "Max: 71.5, C\nsecond OCR line",
                MonitorStatus.Monitoring,
                string.Empty,
                RelayAction.StopSent,
                "Hikmicro:1,2,3,4"));
            recorder.RecordTemperature(new TemperatureSample(
                DateTimeOffset.UtcNow,
                null,
                string.Empty,
                MonitorStatus.NoReading,
                "ocr-empty",
                RelayAction.None,
                "Hikmicro:1,2,3,4"));
            recorder.RecordEvent(
                "relay", "open-all-interlocks", "command-completed", "hard-crash-marker");
            recorder.RecordG2000Telemetry(new ReactorSoftInterlock.Application.G2000.G2000TelemetrySnapshot
            {
                Connected = true,
                CommunicationHealthy = true
            });
            recorder.RecordGasFlow(12.5, enabled: true, "success", "hard-crash-marker");

            var temperaturePath = Path.Combine(recorder.SessionDirectory, "temperature-samples.csv");
            var eventsPath = Path.Combine(recorder.SessionDirectory, "events.csv");
            var g2000Path = Path.Combine(recorder.SessionDirectory, "g2000-telemetry.csv");
            var gasPath = Path.Combine(recorder.SessionDirectory, "gas-flow.csv");
            await WaitUntilAsync(() =>
                FileContainsWhenAvailable(temperaturePath, "second OCR line") &&
                FileContainsWhenAvailable(temperaturePath, "ocr-empty") &&
                FileContainsWhenAvailable(eventsPath, "hard-crash-marker") &&
                HasCsvDataWhenAvailable(g2000Path) &&
                HasCsvDataWhenAvailable(gasPath));

            await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
                root, settings, "2026.07.31-test", uploader: null, CancellationToken.None);
            var bundle = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "experiment-bundles"), "*.zip"));
            using var archive = ZipFile.OpenRead(bundle);
            var summaryEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("report-summary.json"));
            using var summaryDocument = await System.Text.Json.JsonDocument.ParseAsync(summaryEntry.Open());
            var summary = summaryDocument.RootElement;

            Assert.Equal(2, summary.GetProperty("TemperatureSampleCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("ValidTemperatureCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("NoReadingCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("AutomaticTemperatureTripCount").GetInt32());
            Assert.Equal(71.5, summary.GetProperty("MinimumTemperatureC").GetDouble());
            Assert.Equal(71.5, summary.GetProperty("MaximumTemperatureC").GetDouble());
            Assert.Equal(71.5, summary.GetProperty("AverageTemperatureC").GetDouble());
            Assert.Equal(1, summary.GetProperty("EngineeringOpenAllCommandCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("G2000TelemetryCount").GetInt32());
            Assert.Equal(1, summary.GetProperty("GasFlowSampleCount").GetInt32());
            Assert.Equal("recovered-after-unclean-exit", summary.GetProperty("FinalizationReason").GetString());
        }
        finally
        {
            await recorder.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartupRecreatesPendingItemFromCheckpointAfterCrashWindow()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: false);
        string sessionId;
        await using (var first = await ExperimentEvidenceCoordinator.CreateAsync(
                         root, settings, "2026.07.31-test", uploader: null, CancellationToken.None))
        {
            first.RecordEvent("monitoring", "started", "success");
            var result = await first.FinalizeRunAsync(
                settings, "monitoring-stopped", startNextRun: false, CancellationToken.None);
            sessionId = result.SessionId;
            Assert.True(File.Exists(Path.Combine(
                root, "experiment-sessions", sessionId, "bundle-checkpoint.json")));
        }

        settings.ExperimentUpload.AutoUploadEnabled = true;
        await using var recovered = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", uploader: null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(
            root, "experiment-upload-outbox", "pending", $"{sessionId}.json")));
    }

    [Fact]
    public async Task RecoverySkipsSessionWithCorruptManifestInsteadOfPackagingIt()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: false);
        string sessionId;
        await using (var recorder = await ExperimentSessionRecorder.CreateAsync(
                         root, settings, "2026.07.31-test", CancellationToken.None))
        {
            sessionId = recorder.SessionId;
            recorder.RecordEvent("relay", "engineering-action", "success");
            await recorder.FlushAsync(CancellationToken.None);
        }

        await File.WriteAllTextAsync(
            Path.Combine(root, "experiment-sessions", sessionId, "manifest.json"), "{broken");
        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", uploader: null, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(
            root, "experiment-sessions", sessionId, "bundle-checkpoint.json")));
    }

    [Fact]
    public async Task LockedAbandonedSessionDoesNotDisableNewEvidenceRecording()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: false);
        string sessionId;
        await using (var recorder = await ExperimentSessionRecorder.CreateAsync(
                         root, settings, "2026.07.31-test", CancellationToken.None))
        {
            sessionId = recorder.SessionId;
            recorder.RecordEvent("relay", "engineering-action", "success");
            await recorder.FlushAsync(CancellationToken.None);
        }

        await using var lockStream = new FileStream(
            Path.Combine(root, "experiment-sessions", sessionId, "events.csv"),
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", uploader: null, CancellationToken.None);

        Assert.NotEmpty(coordinator.ActiveSessionId);
        Assert.True(File.Exists(Path.Combine(
            root, "experiment-recovery-failures", $"{sessionId}.json")));
    }

    [Fact]
    public async Task CorruptPendingItemIsQuarantinedInsteadOfRetriedForever()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: true);
        var pendingDirectory = Path.Combine(root, "experiment-upload-outbox", "pending");
        Directory.CreateDirectory(pendingDirectory);
        await File.WriteAllTextAsync(Path.Combine(pendingDirectory, "broken.json"), "{broken");

        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", new SuccessfulUploader(), CancellationToken.None);
        await WaitUntilAsync(() => Directory.GetFiles(
            Path.Combine(root, "experiment-upload-outbox", "failed"), "broken.invalid.json").Length == 1);

        Assert.Empty(Directory.GetFiles(pendingDirectory, "broken.json"));
    }

    [Fact]
    public async Task TwoCoordinatorsCannotUploadTheSamePendingItemConcurrently()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: true);
        var bundlePath = Path.Combine(root, "exp-lease.zip");
        await File.WriteAllTextAsync(bundlePath, "bundle");
        var item = await ExperimentUploadWorkItem.CreateAsync(
            "lease-run", bundlePath, CancellationToken.None, target: settings.ExperimentUpload);
        var pendingDirectory = Path.Combine(root, "experiment-upload-outbox", "pending");
        Directory.CreateDirectory(pendingDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(pendingDirectory, "lease-run.json"),
            System.Text.Json.JsonSerializer.Serialize(item));
        var uploader = new BlockingUploader();

        await using var first = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", uploader, CancellationToken.None);
        await uploader.Entered.Task;
        await using var second = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", uploader, CancellationToken.None);
        await Task.Delay(150);

        Assert.Equal(1, uploader.AttemptCount);
        uploader.Release.SetResult();
        await WaitUntilAsync(() => Directory.GetFiles(
            Path.Combine(root, "experiment-upload-outbox", "sent"), "lease-run.json").Length == 1);
    }

    [Fact]
    public async Task LeaseRemainsExclusiveWhileSuccessfulItemMovesToSent()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: true);
        var uploader = new SuccessfulCountingUploader();
        using var moveEntered = new ManualResetEventSlim();
        using var allowMove = new ManualResetEventSlim();
        ExperimentEvidenceCoordinator.BeforeUploadStateMoveForTests = () =>
        {
            moveEntered.Set();
            allowMove.Wait(TimeSpan.FromSeconds(3));
        };

        try
        {
            await using var first = await ExperimentEvidenceCoordinator.CreateAsync(
                root, settings, "2026.07.31-test", uploader, CancellationToken.None);
            first.RecordEvent("monitoring", "started", "success");
            var result = await first.FinalizeRunAsync(
                settings, "monitoring-stopped", startNextRun: true, CancellationToken.None);
            Assert.True(moveEntered.Wait(TimeSpan.FromSeconds(3)));

            await using var second = await ExperimentEvidenceCoordinator.CreateAsync(
                root, settings, "2026.07.31-test", uploader, CancellationToken.None);
            await Task.Delay(150);
            Assert.Equal(1, uploader.AttemptCount);
            Assert.True(File.Exists(Path.Combine(
                root, "experiment-upload-outbox", "uploading", $"{result.SessionId}.json")));

            allowMove.Set();
            await WaitUntilAsync(() => File.Exists(Path.Combine(
                root, "experiment-upload-outbox", "sent", $"{result.SessionId}.json")));
            Assert.Equal(1, uploader.AttemptCount);
        }
        finally
        {
            allowMove.Set();
            ExperimentEvidenceCoordinator.BeforeUploadStateMoveForTests = null;
        }
    }

    [Fact]
    public async Task StartupImmediatelyRecoversUploadingLeaseLeftByCrashedProcess()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: true);
        var bundlePath = Path.Combine(root, "exp-crash.zip");
        await File.WriteAllTextAsync(bundlePath, "bundle");
        var item = await ExperimentUploadWorkItem.CreateAsync(
            "crash-run", bundlePath, CancellationToken.None, target: settings.ExperimentUpload);
        var uploadingDirectory = Path.Combine(root, "experiment-upload-outbox", "uploading");
        Directory.CreateDirectory(uploadingDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(uploadingDirectory, "crash-run.json"),
            System.Text.Json.JsonSerializer.Serialize(item));

        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", new SuccessfulUploader(), CancellationToken.None);
        await WaitUntilAsync(() => File.Exists(Path.Combine(
            root, "experiment-upload-outbox", "sent", "crash-run.json")));

        Assert.False(File.Exists(Path.Combine(uploadingDirectory, "crash-run.json")));
    }

    [Fact]
    public async Task RedirectResponseMovesItemToFailedInsteadOfRetrying()
    {
        var root = CreateTemporaryDirectory();
        var settings = CreateSettings(root, autoUpload: true);
        await using var coordinator = await ExperimentEvidenceCoordinator.CreateAsync(
            root, settings, "2026.07.31-test", new RedirectUploader(), CancellationToken.None);
        coordinator.RecordEvent("monitoring", "started", "success");
        var result = await coordinator.FinalizeRunAsync(
            settings, "monitoring-stopped", startNextRun: true, CancellationToken.None);
        await WaitUntilAsync(() => File.Exists(Path.Combine(
            root, "experiment-upload-outbox", "failed", $"{result.SessionId}.json")));

        Assert.Empty(Directory.GetFiles(
            Path.Combine(root, "experiment-upload-outbox", "pending"), $"{result.SessionId}.json"));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!predicate())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static bool FileContainsWhenAvailable(string path, string value)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains(value, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool HasCsvDataWhenAvailable(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            _ = reader.ReadLine();
            return reader.ReadLine() is not null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static AppSettings CreateSettings(string root, bool autoUpload) => new()
    {
        DataDirectory = root,
        ExperimentUpload = new ExperimentUploadSettings { AutoUploadEnabled = autoUpload }
    };

    private static TemperatureSample CreateSample(double temperature, string raw) => new(
        DateTimeOffset.UtcNow,
        temperature,
        raw,
        MonitorStatus.Monitoring,
        string.Empty,
        RelayAction.None,
        "Hikmicro:1,2,3,4");

    private static void AssertBundleContains(string path, string included, string excluded)
    {
        using var archive = ZipFile.OpenRead(path);
        var text = string.Join(
            Environment.NewLine,
            new[] { "events.csv", "temperature-samples.csv" }.Select(fileName =>
            {
                var entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry(fileName));
                using var reader = new StreamReader(entry.Open());
                return reader.ReadToEnd();
            }));
        Assert.Contains(included, text);
        Assert.DoesNotContain(excluded, text);
        Assert.NotNull(archive.GetEntry("lab-profile.json"));
        Assert.NotNull(archive.GetEntry("experiment-context.md"));
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class AlwaysOfflineUploader : IExperimentPackageUploader
    {
        public Task<ExperimentUploadReceipt> UploadAsync(
            ExperimentUploadWorkItem item,
            ExperimentUploadSettings settings,
            CancellationToken cancellationToken) =>
           throw new HttpRequestException("offline secret-token");
    }

    private sealed class MissingThenSuccessUploader : IExperimentPackageUploader
    {
        public int AttemptCount { get; private set; }

        public Task<ExperimentUploadReceipt> UploadAsync(
            ExperimentUploadWorkItem item,
            ExperimentUploadSettings settings,
            CancellationToken cancellationToken)
        {
            AttemptCount++;
            if (AttemptCount == 1)
            {
                throw new ExperimentCredentialMissingException();
            }

            return SuccessfulUploader.CreateReceiptAsync(item);
        }
    }

    private sealed class SuccessfulUploader : IExperimentPackageUploader
    {
        public Task<ExperimentUploadReceipt> UploadAsync(
            ExperimentUploadWorkItem item,
            ExperimentUploadSettings settings,
            CancellationToken cancellationToken) => CreateReceiptAsync(item);

        public static Task<ExperimentUploadReceipt> CreateReceiptAsync(
            ExperimentUploadWorkItem item) => Task.FromResult(new ExperimentUploadReceipt
            {
                SessionId = item.SessionId,
                FileName = item.FileName,
                SizeBytes = item.SizeBytes,
                Sha256 = item.Sha256,
                UploadedAtUtc = DateTimeOffset.UtcNow,
                RemoteUrl = "https://gitlab.example/package"
            });
    }

    private sealed class BlockingUploader : IExperimentPackageUploader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AttemptCount { get; private set; }

        public async Task<ExperimentUploadReceipt> UploadAsync(
            ExperimentUploadWorkItem item,
            ExperimentUploadSettings settings,
            CancellationToken cancellationToken)
        {
            AttemptCount++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await SuccessfulUploader.CreateReceiptAsync(item);
        }
    }

    private sealed class SuccessfulCountingUploader : IExperimentPackageUploader
    {
        public int AttemptCount { get; private set; }

        public Task<ExperimentUploadReceipt> UploadAsync(
            ExperimentUploadWorkItem item,
            ExperimentUploadSettings settings,
            CancellationToken cancellationToken)
        {
            AttemptCount++;
            return SuccessfulUploader.CreateReceiptAsync(item);
        }
    }

    private sealed class RedirectUploader : IExperimentPackageUploader
    {
        public Task<ExperimentUploadReceipt> UploadAsync(
            ExperimentUploadWorkItem item,
            ExperimentUploadSettings settings,
            CancellationToken cancellationToken) =>
            throw new ExperimentUploadException(System.Net.HttpStatusCode.Found);
    }
}
