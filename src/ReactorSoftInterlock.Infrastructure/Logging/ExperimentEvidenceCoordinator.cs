using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualBasic.FileIO;
using ReactorSoftInterlock.Application.G2000;
using ReactorSoftInterlock.Domain;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Logging;

public sealed class ExperimentFinalizationResult
{
    public bool Created { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string BundlePath { get; init; } = string.Empty;
    public string FailureKind { get; init; } = string.Empty;
}

/// <summary>
/// Rotates independent experiment runs and manages packaging/upload outside the
/// safety-critical monitoring and output-control path.
/// </summary>
public sealed class ExperimentEvidenceCoordinator : IAsyncDisposable
{
    private const int TransitionBufferCapacity = 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _dataDirectory;
    private readonly string _softwareVersion;
    private readonly IExperimentPackageUploader? _uploader;
    private readonly Func<string, AppSettings, string, CancellationToken, Task<ExperimentSessionRecorder>> _recorderFactory;
    private readonly SemaphoreSlim _rotationGate = new(1, 1);
    private readonly SemaphoreSlim _uploadSignal = new(0, 1);
    private readonly CancellationTokenSource _uploadCancellation = new();
    private readonly Task _uploadWorker;
    private readonly object _routingLock = new();
    private readonly Queue<RoutedRecord> _transitionBuffer = new();
    private ExperimentSessionRecorder? _activeRecorder;
    private RoutedTelemetry? _bufferedTelemetry;
    private RoutedSettings? _bufferedSettings;
    private long _droppedTransitionRecords;
    private long _droppedTransitionTelemetry;
    private bool _routingTransition;
    private ExperimentUploadSettings _uploadSettings;
    private int _disposed;
    private ExperimentUploadState _uploadState;
    private int _forceCredentialRetry;

    internal static Action? BeforeUploadStateMoveForTests { get; set; }

    private ExperimentEvidenceCoordinator(
        string dataDirectory,
        string softwareVersion,
        ExperimentSessionRecorder activeRecorder,
        ExperimentUploadSettings uploadSettings,
        IExperimentPackageUploader? uploader,
        Func<string, AppSettings, string, CancellationToken, Task<ExperimentSessionRecorder>> recorderFactory)
    {
        _dataDirectory = dataDirectory;
        _softwareVersion = softwareVersion;
        _activeRecorder = activeRecorder;
        _uploadSettings = CloneUploadSettings(uploadSettings);
        _uploader = uploader;
        _recorderFactory = recorderFactory;
        _uploadState = uploadSettings.AutoUploadEnabled
            ? ExperimentUploadState.Pending
            : ExperimentUploadState.Disabled;
        _uploadWorker = Task.Run(UploadWorkerAsync);
    }

    public event Action<ExperimentUploadState>? UploadStateChanged;

    public string ActiveSessionId
    {
        get { lock (_routingLock) { return _activeRecorder?.SessionId ?? string.Empty; } }
    }

    public string SessionId => ActiveSessionId;

    public string ActiveSessionDirectory
    {
        get { lock (_routingLock) { return _activeRecorder?.SessionDirectory ?? string.Empty; } }
    }

    public ExperimentUploadState UploadState => _uploadState;

    public string SuggestedBundleFileName
    {
        get
        {
            ExperimentSessionRecorder? recorder;
            lock (_routingLock) { recorder = _activeRecorder; }
            return recorder is null
                ? $"exp-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip"
                : Path.GetFileName(BuildShortBundlePath(recorder.SessionId, recorder.StartedAt));
        }
    }

    public static async Task<ExperimentEvidenceCoordinator> CreateAsync(
        string dataDirectory,
        AppSettings settings,
        string softwareVersion,
        IExperimentPackageUploader? uploader,
        CancellationToken cancellationToken)
        => await CreateCoreAsync(
            dataDirectory,
            settings,
            softwareVersion,
            uploader,
            static (directory, snapshot, version, token) => ExperimentSessionRecorder.CreateAsync(
                directory, snapshot, version, token),
            cancellationToken).ConfigureAwait(false);

    internal static Task<ExperimentEvidenceCoordinator> CreateWithRecorderFactoryAsync(
        string dataDirectory,
        AppSettings settings,
        string softwareVersion,
        IExperimentPackageUploader? uploader,
        Func<string, AppSettings, string, CancellationToken, Task<ExperimentSessionRecorder>> recorderFactory,
        CancellationToken cancellationToken) => CreateCoreAsync(
            dataDirectory, settings, softwareVersion, uploader, recorderFactory, cancellationToken);

    private static async Task<ExperimentEvidenceCoordinator> CreateCoreAsync(
        string dataDirectory,
        AppSettings settings,
        string softwareVersion,
        IExperimentPackageUploader? uploader,
        Func<string, AppSettings, string, CancellationToken, Task<ExperimentSessionRecorder>> recorderFactory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(settings);
        settings.ExperimentUpload ??= new ExperimentUploadSettings();
        settings.ExperimentUpload.Normalize();
        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(Path.Combine(dataDirectory, "experiment-bundles"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "experiment-upload-outbox", "pending"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "experiment-upload-outbox", "uploading"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "experiment-upload-outbox", "sent"));
        Directory.CreateDirectory(Path.Combine(dataDirectory, "experiment-upload-outbox", "failed"));

        await RecoverStaleUploadLeasesAsync(dataDirectory, cancellationToken).ConfigureAwait(false);
        await RecoverAbandonedRunsAsync(dataDirectory, settings, cancellationToken).ConfigureAwait(false);
        var recorder = await recorderFactory(
            dataDirectory, settings, softwareVersion, cancellationToken).ConfigureAwait(false);
        var coordinator = new ExperimentEvidenceCoordinator(
            dataDirectory,
            string.IsNullOrWhiteSpace(softwareVersion) ? "unknown" : softwareVersion,
            recorder,
            settings.ExperimentUpload,
            uploader,
            recorderFactory);
        coordinator.SignalUploadWorker();
        return coordinator;
    }

    public void RecordTemperature(TemperatureSample sample)
    {
        Route(new RoutedTemperature(sample));
    }

    public void RecordEvent(string category, string action, string outcome = "", string details = "")
    {
        Route(new RoutedEvent(
            DateTimeOffset.UtcNow, category ?? string.Empty, action ?? string.Empty,
            outcome ?? string.Empty, details ?? string.Empty));
    }

    public void RecordG2000Telemetry(G2000TelemetrySnapshot snapshot)
    {
        var immutableSnapshot = snapshot.Clone();
        Route(new RoutedTelemetry(DateTimeOffset.UtcNow, immutableSnapshot));
    }

    public void RecordGasFlow(double? actualFlow, bool enabled, string outcome, string details = "")
    {
        Route(new RoutedGasFlow(
            DateTimeOffset.UtcNow, actualFlow, enabled, outcome ?? string.Empty, details ?? string.Empty));
    }

    public void UpdateSettings(AppSettings settings)
    {
        try
        {
            var settingsSnapshot = JsonSerializer.Deserialize<AppSettings>(
                JsonSerializer.Serialize(settings, JsonOptions), JsonOptions) ?? settings;
            Route(new RoutedSettings(settingsSnapshot));
            settings.ExperimentUpload ??= new ExperimentUploadSettings();
            _uploadSettings = CloneUploadSettings(settings.ExperimentUpload);
            SetUploadState(_uploadSettings.AutoUploadEnabled
                ? ExperimentUploadState.Pending
                : ExperimentUploadState.Disabled);
            SignalUploadWorker();
        }
        catch
        {
            // Evidence settings must never affect application settings or control.
        }
    }

    public void NotifyCredentialAvailability(bool available)
    {
        if (!_uploadSettings.AutoUploadEnabled)
        {
            SetUploadState(ExperimentUploadState.Disabled);
        }
        else if (!available)
        {
            SetUploadState(ExperimentUploadState.MissingCredential);
        }
        else
        {
            Interlocked.Exchange(ref _forceCredentialRetry, 1);
            SetUploadState(ExperimentUploadState.Pending);
            SignalUploadWorker();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        ExperimentSessionRecorder? recorder;
        lock (_routingLock) { recorder = _activeRecorder; }
        if (recorder is not null)
        {
            await recorder.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ExportCurrentAsync(string destinationZipPath, CancellationToken cancellationToken)
    {
        ExperimentSessionRecorder? recorder;
        lock (_routingLock) { recorder = _activeRecorder; }
        if (recorder is null)
        {
            throw new InvalidOperationException("No active experiment run is available.");
        }
        await recorder.ExportAsync(destinationZipPath, cancellationToken).ConfigureAwait(false);
    }

    public Task ExportAsync(string destinationZipPath, CancellationToken cancellationToken) =>
        ExportCurrentAsync(destinationZipPath, cancellationToken);

    public async Task<ExperimentFinalizationResult> FinalizeRunAsync(
        AppSettings settings,
        string finalizationReason,
        bool startNextRun,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new ExperimentFinalizationResult { FailureKind = "coordinator-disposed" };
        }

        var gateAcquired = false;
        var transitionOpen = false;
        ExperimentSessionRecorder? oldRecorder = null;
        try
        {
            await _rotationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            if (Volatile.Read(ref _disposed) != 0)
            {
                return new ExperimentFinalizationResult { FailureKind = "coordinator-disposed" };
            }

            oldRecorder = BeginRoutingTransition();
            transitionOpen = true;
            if (oldRecorder is null)
            {
                CompleteRoutingTransition(null, keepActive: false);
                transitionOpen = false;
                return new ExperimentFinalizationResult();
            }

            settings.ExperimentUpload ??= new ExperimentUploadSettings();
            _uploadSettings = CloneUploadSettings(settings.ExperimentUpload);
            ExperimentSessionRecorder? nextRecorder = null;
            if (startNextRun)
            {
                nextRecorder = await _recorderFactory(
                    _dataDirectory, settings, _softwareVersion, cancellationToken).ConfigureAwait(false);
            }

            CompleteRoutingTransition(
                startNextRun ? nextRecorder : oldRecorder,
                keepActive: startNextRun);
            transitionOpen = false;
            if (!oldRecorder.HasActivity)
            {
                await oldRecorder.CompleteAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
                return new ExperimentFinalizationResult { SessionId = oldRecorder.SessionId };
            }

            SetUploadState(ExperimentUploadState.Packaging);
            oldRecorder.RecordEvent(
                "application", "run-finalizing", "success", finalizationReason);
            var bundlePath = BuildShortBundlePath(oldRecorder.SessionId, oldRecorder.StartedAt);
            await oldRecorder.FinalizeAndExportAsync(
                bundlePath, finalizationReason, cancellationToken).ConfigureAwait(false);

            var item = await ExperimentUploadWorkItem.CreateAsync(
                oldRecorder.SessionId,
                bundlePath,
                cancellationToken,
                oldRecorder.SessionDirectory,
                _uploadSettings).ConfigureAwait(false);
            await WriteBundleCheckpointAsync(
                oldRecorder.SessionDirectory, item, recovered: false, cancellationToken)
                .ConfigureAwait(false);
            if (_uploadSettings.AutoUploadEnabled)
            {
                await WritePendingItemAsync(item, cancellationToken).ConfigureAwait(false);
                SetUploadState(ExperimentUploadState.Pending);
                SignalUploadWorker();
            }
            else
            {
                SetUploadState(ExperimentUploadState.Disabled);
            }

            return new ExperimentFinalizationResult
            {
                Created = true,
                SessionId = oldRecorder.SessionId,
                BundlePath = bundlePath
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetUploadState(ExperimentUploadState.Failed);
            return new ExperimentFinalizationResult { FailureKind = "cancelled" };
        }
        catch (Exception ex)
        {
            SetUploadState(ExperimentUploadState.Failed);
            return new ExperimentFinalizationResult { FailureKind = ClassifyFailure(ex) };
        }
        finally
        {
            if (transitionOpen)
            {
                RestoreRoutingAfterFailure(oldRecorder);
            }

            if (gateAcquired && Volatile.Read(ref _disposed) != 0)
            {
                ExperimentSessionRecorder? recorderAfterDispose;
                lock (_routingLock)
                {
                    recorderAfterDispose = _activeRecorder;
                    _activeRecorder = null;
                    _routingTransition = false;
                }

                if (recorderAfterDispose is not null)
                {
                    try
                    {
                        await recorderAfterDispose.CompleteAsync(TimeSpan.FromSeconds(5))
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Process exit recovery owns any raw run that could not close here.
                    }
                }
            }

            if (gateAcquired)
            {
                _rotationGate.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var gateAcquired = false;
        try
        {
            gateAcquired = await _rotationGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch
        {
            // A finalization already in progress keeps ownership of the gate.
        }

        if (gateAcquired)
        {
            ExperimentSessionRecorder? recorder;
            List<RoutedRecord> bufferedRecords;
            lock (_routingLock)
            {
                recorder = _activeRecorder;
                bufferedRecords = TakeBufferedBatchLocked();

                _activeRecorder = null;
                _routingTransition = false;
            }

            if (recorder is not null)
            {
                foreach (var buffered in bufferedRecords)
                {
                    try { buffered.Apply(recorder); } catch { }
                }

                try { await recorder.CompleteAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            }
        }

        _uploadCancellation.Cancel();
        SignalUploadWorker();
        try { await _uploadWorker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _uploadCancellation.Dispose();
        _uploadSignal.Dispose();
        if (gateAcquired)
        {
            _rotationGate.Release();
            _rotationGate.Dispose();
        }
    }

    private async Task UploadWorkerAsync()
    {
        var cancellationToken = _uploadCancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueUploadsAsync(cancellationToken).ConfigureAwait(false);
                await _uploadSignal.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                SetUploadState(ExperimentUploadState.Failed);
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Route(RoutedRecord record)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            lock (_routingLock)
            {
                if (_routingTransition)
                {
                    BufferRecordLocked(record);
                    return;
                }

                if (_activeRecorder is not null)
                {
                    record.Apply(_activeRecorder);
                }
            }
        }
        catch
        {
            // Evidence routing must never propagate into hardware or monitoring callbacks.
        }
    }

    private ExperimentSessionRecorder? BeginRoutingTransition()
    {
        lock (_routingLock)
        {
            _routingTransition = true;
            return _activeRecorder;
        }
    }

    private void CompleteRoutingTransition(ExperimentSessionRecorder? target, bool keepActive)
    {
        lock (_routingLock)
        {
            _activeRecorder = keepActive ? target : null;
        }

        DrainRoutingTransition(target);
    }

    private void DrainRoutingTransition(ExperimentSessionRecorder? target)
    {
        while (true)
        {
            List<RoutedRecord> bufferedRecords;
            lock (_routingLock)
            {
                bufferedRecords = TakeBufferedBatchLocked();
                if (bufferedRecords.Count == 0)
                {
                    _routingTransition = false;
                    return;
                }
            }

            if (target is null)
            {
                continue;
            }

            foreach (var buffered in bufferedRecords)
            {
                try { buffered.Apply(target); } catch { }
            }
        }
    }

    private void BufferRecordLocked(RoutedRecord record)
    {
        switch (record)
        {
            case RoutedTelemetry telemetry:
                if (_bufferedTelemetry is not null)
                {
                    _droppedTransitionTelemetry++;
                }
                _bufferedTelemetry = telemetry;
                return;
            case RoutedSettings settings:
                _bufferedSettings = settings;
                return;
        }

        if (_transitionBuffer.Count >= TransitionBufferCapacity)
        {
            _droppedTransitionRecords++;
            return;
        }

        _transitionBuffer.Enqueue(record);
    }

    private List<RoutedRecord> TakeBufferedBatchLocked()
    {
        var batch = new List<RoutedRecord>(_transitionBuffer.Count + 3);
        while (_transitionBuffer.TryDequeue(out var buffered))
        {
            batch.Add(buffered);
        }

        if (_bufferedTelemetry is not null)
        {
            batch.Add(_bufferedTelemetry);
            _bufferedTelemetry = null;
        }

        if (_bufferedSettings is not null)
        {
            batch.Add(_bufferedSettings);
            _bufferedSettings = null;
        }

        if (_droppedTransitionRecords > 0 || _droppedTransitionTelemetry > 0)
        {
            batch.Add(new RoutedEvent(
                DateTimeOffset.UtcNow,
                "evidence",
                "transition-buffer-dropped",
                "warning",
                $"records={_droppedTransitionRecords}; telemetry={_droppedTransitionTelemetry}"));
            _droppedTransitionRecords = 0;
            _droppedTransitionTelemetry = 0;
        }

        return batch;
    }

    private void RestoreRoutingAfterFailure(ExperimentSessionRecorder? oldRecorder)
    {
        lock (_routingLock)
        {
            _activeRecorder = oldRecorder;
        }

        DrainRoutingTransition(oldRecorder);
    }

    private async Task ProcessDueUploadsAsync(CancellationToken cancellationToken)
    {
        var settings = CloneUploadSettings(_uploadSettings);
        if (!settings.AutoUploadEnabled)
        {
            SetUploadState(ExperimentUploadState.Disabled);
            return;
        }

        if (_uploader is null)
        {
            SetUploadState(ExperimentUploadState.Failed);
            return;
        }

        var pendingDirectory = GetPendingDirectory();
        var forceCredentialRetry = Interlocked.Exchange(ref _forceCredentialRetry, 0) != 0;
        foreach (var path in Directory.GetFiles(pendingDirectory, "*.json").OrderBy(value => value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var lease = TryAcquireUploadLease(path);
            if (lease is null)
            {
                continue;
            }

            ExperimentUploadWorkItem? item;
            try
            {
                lease.Stream.Position = 0;
                item = await JsonSerializer.DeserializeAsync<ExperimentUploadWorkItem>(
                    lease.Stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                MoveOutboxFile(lease, "failed", ".invalid.json");
                SetUploadState(ExperimentUploadState.Failed);
                continue;
            }

            if (item is null)
            {
                MoveOutboxFile(lease, "failed", ".invalid.json");
                SetUploadState(ExperimentUploadState.Failed);
                continue;
            }

            var credentialFailure =
                string.Equals(item.LastFailureKind, "missing-credential", StringComparison.Ordinal) ||
                string.Equals(item.LastFailureKind, "unauthorized", StringComparison.Ordinal);
            if (item.NextAttemptAtUtc > DateTimeOffset.UtcNow &&
                !(forceCredentialRetry && credentialFailure))
            {
                ReturnLeaseToPending(lease);
                SetUploadState(ExperimentUploadState.Pending);
                continue;
            }

            try
            {
                SetUploadState(ExperimentUploadState.Uploading);
                var receipt = await _uploader.UploadAsync(item, settings, cancellationToken)
                    .ConfigureAwait(false);
                await MarkSentAsync(lease, item, receipt, cancellationToken).ConfigureAwait(false);
                lease.Dispose();
                SetUploadState(ExperimentUploadState.Uploaded);
            }
            catch (ExperimentCredentialMissingException)
            {
                item.LastFailureKind = "missing-credential";
                item.NextAttemptAtUtc = DateTimeOffset.MaxValue;
                await PersistLeaseItemAsync(lease, item, cancellationToken).ConfigureAwait(false);
                ReturnLeaseToPending(lease);
                SetUploadState(ExperimentUploadState.MissingCredential);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ReturnLeaseToPending(lease);
                throw;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                item.AttemptCount++;
                item.LastFailureKind = ClassifyFailure(ex);
                if (IsPermanentFailure(ex))
                {
                    await PersistLeaseItemAsync(lease, item, cancellationToken).ConfigureAwait(false);
                    MoveOutboxFile(lease, "failed", ".json");
                    SetUploadState(ExperimentUploadState.Failed);
                    continue;
                }

                if (IsAuthenticationFailure(ex))
                {
                    item.NextAttemptAtUtc = DateTimeOffset.MaxValue;
                    await PersistLeaseItemAsync(lease, item, cancellationToken).ConfigureAwait(false);
                    ReturnLeaseToPending(lease);
                    SetUploadState(ExperimentUploadState.MissingCredential);
                    continue;
                }

                var retryAfter = (ex as ExperimentUploadException)?.RetryAfter;
                item.NextAttemptAtUtc = DateTimeOffset.UtcNow.Add(
                    retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero
                        ? retryAfter.Value
                        : GetRetryDelay(item.AttemptCount));
                await PersistLeaseItemAsync(lease, item, cancellationToken).ConfigureAwait(false);
                ReturnLeaseToPending(lease);
                SetUploadState(ExperimentUploadState.Failed);
            }
        }
    }

    private async Task WritePendingItemAsync(
        ExperimentUploadWorkItem item,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(GetPendingDirectory(), $"{MakeSafeName(item.SessionId)}.json");
        await WritePendingItemAtPathAsync(path, item, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteBundleCheckpointAsync(
        string sessionDirectory,
        ExperimentUploadWorkItem item,
        bool recovered,
        CancellationToken cancellationToken)
    {
        var checkpoint = new ExperimentBundleCheckpoint
        {
            SessionId = item.SessionId,
            BundlePath = item.BundlePath,
            FileName = item.FileName,
            SizeBytes = item.SizeBytes,
            Sha256 = item.Sha256,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            RecoveredAfterUncleanExit = recovered,
            TargetBaseUrl = item.TargetBaseUrl,
            TargetProjectId = item.TargetProjectId,
            TargetPackageName = item.TargetPackageName,
            TargetCredentialName = item.TargetCredentialName
        };
        var path = Path.Combine(sessionDirectory, "bundle-checkpoint.json");
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(checkpoint, JsonOptions),
                Utf8WithoutBom,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static async Task WritePendingItemAtPathAsync(
        string path,
        ExperimentUploadWorkItem item,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(item, JsonOptions);
            await File.WriteAllTextAsync(
                temporaryPath, json, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private async Task MarkSentAsync(
        UploadLease lease,
        ExperimentUploadWorkItem item,
        ExperimentUploadReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(item.SessionDirectory) && Directory.Exists(item.SessionDirectory))
        {
            var receiptPath = Path.Combine(item.SessionDirectory, "upload-receipt.json");
            await File.WriteAllTextAsync(
                receiptPath,
                JsonSerializer.Serialize(receipt, JsonOptions),
                Utf8WithoutBom,
                cancellationToken).ConfigureAwait(false);
        }

        var sentPath = Path.Combine(
            _dataDirectory,
            "experiment-upload-outbox",
            "sent",
            Path.GetFileName(lease.Path));
        lease.CloseDataStream();
        InvokeBeforeUploadStateMoveForTests();
        File.Move(lease.Path, sentPath, overwrite: true);
    }

    private string GetPendingDirectory() =>
        Path.Combine(_dataDirectory, "experiment-upload-outbox", "pending");

    private UploadLease? TryAcquireUploadLease(string pendingPath)
    {
        var leasePath = Path.Combine(
            _dataDirectory, "experiment-upload-outbox", "uploading", Path.GetFileName(pendingPath));
        var lockPath = $"{leasePath}.lock";
        FileStream? lockStream = null;
        try
        {
            lockStream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            File.Move(pendingPath, leasePath, overwrite: false);
            return new UploadLease(leasePath, lockPath, lockStream);
        }
        catch (IOException)
        {
            lockStream?.Dispose();
            if (lockStream is not null)
            {
                try { File.Delete(lockPath); } catch { }
            }
            return null;
        }
        catch
        {
            lockStream?.Dispose();
            try { File.Move(leasePath, pendingPath, overwrite: true); } catch { }
            try { File.Delete(lockPath); } catch { }
            throw;
        }
    }

    private static async Task PersistLeaseItemAsync(
        UploadLease lease,
        ExperimentUploadWorkItem item,
        CancellationToken cancellationToken)
    {
        lease.Stream.Position = 0;
        lease.Stream.SetLength(0);
        await JsonSerializer.SerializeAsync(
            lease.Stream, item, JsonOptions, cancellationToken).ConfigureAwait(false);
        await lease.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ReturnLeaseToPending(UploadLease lease)
    {
        var pendingPath = Path.Combine(GetPendingDirectory(), Path.GetFileName(lease.Path));
        lease.CloseDataStream();
        InvokeBeforeUploadStateMoveForTests();
        File.Move(lease.Path, pendingPath, overwrite: true);
        lease.Dispose();
    }

    private void MoveOutboxFile(UploadLease lease, string stateDirectory, string suffix)
    {
        var baseName = Path.GetFileNameWithoutExtension(lease.Path);
        var destination = Path.Combine(
            _dataDirectory, "experiment-upload-outbox", stateDirectory, $"{baseName}{suffix}");
        lease.CloseDataStream();
        InvokeBeforeUploadStateMoveForTests();
        File.Move(lease.Path, destination, overwrite: true);
        lease.Dispose();
    }

    private static void InvokeBeforeUploadStateMoveForTests()
    {
        try { BeforeUploadStateMoveForTests?.Invoke(); } catch { }
    }

    private string BuildShortBundlePath(string sessionId, DateTimeOffset startedAt)
    {
        var suffix = sessionId[(sessionId.LastIndexOf('-') + 1)..];
        if (suffix.Length > 8)
        {
            suffix = suffix[..8];
        }

        var fileName = $"exp-{startedAt.UtcDateTime:yyyyMMdd-HHmmss}-{MakeSafeName(suffix)}.zip";
        return Path.Combine(_dataDirectory, "experiment-bundles", fileName);
    }

    private void SetUploadState(ExperimentUploadState state)
    {
        if (_uploadState == state)
        {
            return;
        }

        _uploadState = state;
        try { UploadStateChanged?.Invoke(state); } catch { }
    }

    private void SignalUploadWorker()
    {
        try
        {
            if (_uploadSignal.CurrentCount == 0)
            {
                _uploadSignal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal static TimeSpan GetRetryDelay(int attemptCount) => attemptCount switch
    {
        <= 1 => TimeSpan.FromSeconds(30),
        2 => TimeSpan.FromMinutes(2),
        3 => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromMinutes(30)
    };

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        ExperimentCredentialMissingException => "missing-credential",
        ExperimentUploadException upload when
            upload.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            "unauthorized",
        ExperimentUploadException upload when upload.StatusCode == (System.Net.HttpStatusCode)429 => "rate-limited",
        ExperimentUploadException upload when (int?)upload.StatusCode is >= 300 and < 400 => "redirect-rejected",
        ExperimentUploadException upload when (int?)upload.StatusCode >= 500 => "server-error",
        ExperimentLocalIntegrityException => "local-integrity",
        TimeoutException => "timeout",
        TaskCanceledException => "timeout",
        FileNotFoundException => "missing-local-bundle",
        HttpRequestException => "network",
        IOException => "local-io",
        _ => "unexpected"
    };

    private static bool IsAuthenticationFailure(Exception exception) =>
        exception is ExperimentUploadException
        {
            StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
        };

    private static bool IsPermanentFailure(Exception exception) => exception switch
    {
        ExperimentLocalIntegrityException or FileNotFoundException => true,
        ExperimentUploadException upload when (int?)upload.StatusCode is >= 300 and < 400 => true,
        ExperimentUploadException upload when upload.StatusCode is not null &&
            (int)upload.StatusCode is >= 400 and < 500 &&
            upload.StatusCode is not System.Net.HttpStatusCode.RequestTimeout &&
            upload.StatusCode != (System.Net.HttpStatusCode)429 &&
            !IsAuthenticationFailure(upload) => true,
        InvalidOperationException => true,
        _ => false
    };

    private static ExperimentUploadSettings CloneUploadSettings(ExperimentUploadSettings source)
    {
        var clone = new ExperimentUploadSettings
        {
            AutoUploadEnabled = source.AutoUploadEnabled,
            BaseUrl = source.BaseUrl,
            ProjectId = source.ProjectId,
            PackageName = source.PackageName,
            CredentialTarget = source.CredentialTarget,
            HttpTimeoutSeconds = source.HttpTimeoutSeconds
        };
        clone.Normalize();
        return clone;
    }

    private static string MakeSafeName(string value) =>
        new(value.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_'
            ? character
            : '_').ToArray());

    private static async Task RecoverAbandonedRunsAsync(
        string dataDirectory,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var sessionsDirectory = Path.Combine(dataDirectory, "experiment-sessions");
        if (!Directory.Exists(sessionsDirectory))
        {
            return;
        }

        var profile = await LabProfileStore.LoadOrCreateAsync(dataDirectory, cancellationToken)
            .ConfigureAwait(false);
        foreach (var sessionDirectory in Directory.GetDirectories(sessionsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!HasRecordedActivity(sessionDirectory))
                {
                    continue;
                }

                var checkpointItem = await ReadUsableBundleCheckpointAsync(
                    sessionDirectory, settings.ExperimentUpload, cancellationToken).ConfigureAwait(false);
                if (checkpointItem is not null)
                {
                    if (settings.ExperimentUpload.AutoUploadEnabled &&
                        !HasOutboxArtifact(dataDirectory, checkpointItem.SessionId) &&
                        !File.Exists(Path.Combine(sessionDirectory, "upload-receipt.json")))
                    {
                        var pendingDirectory = Path.Combine(
                            dataDirectory, "experiment-upload-outbox", "pending");
                        Directory.CreateDirectory(pendingDirectory);
                        await WritePendingItemAtPathAsync(
                            Path.Combine(pendingDirectory, $"{MakeSafeName(checkpointItem.SessionId)}.json"),
                            checkpointItem,
                            cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var manifestPath = Path.Combine(sessionDirectory, "manifest.json");
                var summaryPath = Path.Combine(sessionDirectory, "report-summary.json");
                if (!File.Exists(manifestPath) || !File.Exists(summaryPath))
                {
                    continue;
                }

                var sessionId = Path.GetFileName(sessionDirectory);
                var recoveredAt = DateTimeOffset.UtcNow;
                var startedAt = ReadStartedAt(manifestPath) ?? recoveredAt;
                if (!await UpdateRecoveredJsonCheckpointAsync(
                        manifestPath, recoveredAt, cancellationToken).ConfigureAwait(false) ||
                    !await RebuildRecoveredSummaryAsync(
                        summaryPath,
                        sessionDirectory,
                        startedAt,
                        recoveredAt,
                        cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var runProfile = await LoadRunProfileAsync(
                    sessionDirectory, profile, cancellationToken).ConfigureAwait(false);
                var runSettings = await LoadRunSettingsAsync(
                    sessionDirectory, settings, cancellationToken).ConfigureAwait(false);
                await WriteTextAtomicallyAsync(
                    Path.Combine(sessionDirectory, "experiment-context.md"),
                    LabProfileStore.RenderExperimentContext(
                        runProfile,
                        runSettings,
                        sessionId,
                        startedAt,
                        recoveredAt,
                        "recovered-after-unclean-exit"),
                    cancellationToken).ConfigureAwait(false);
                if (!IsValidJson(manifestPath) ||
                    !IsValidJson(summaryPath) ||
                    !IsValidJson(Path.Combine(sessionDirectory, LabProfileStore.FileName)))
                {
                    continue;
                }

                var suffix = sessionId[(sessionId.LastIndexOf('-') + 1)..];
                suffix = MakeSafeName(suffix.Length > 8 ? suffix[..8] : suffix);
                var bundleDirectory = Path.Combine(dataDirectory, "experiment-bundles");
                Directory.CreateDirectory(bundleDirectory);
                var bundlePath = Path.Combine(
                    bundleDirectory,
                    $"exp-{startedAt.UtcDateTime:yyyyMMdd-HHmmss}-{suffix}.zip");
                CreateRecoveredArchive(sessionDirectory, bundlePath);
                if (!File.Exists(bundlePath))
                {
                    continue;
                }

                var item = await ExperimentUploadWorkItem.CreateAsync(
                    sessionId,
                    bundlePath,
                    cancellationToken,
                    sessionDirectory,
                    settings.ExperimentUpload).ConfigureAwait(false);
                await WriteBundleCheckpointAsync(
                    sessionDirectory, item, recovered: true, cancellationToken).ConfigureAwait(false);
                if (settings.ExperimentUpload.AutoUploadEnabled)
                {
                    var pendingDirectory = Path.Combine(
                        dataDirectory, "experiment-upload-outbox", "pending");
                    Directory.CreateDirectory(pendingDirectory);
                    await WritePendingItemAtPathAsync(
                        Path.Combine(pendingDirectory, $"{MakeSafeName(sessionId)}.json"),
                        item,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    var failureDirectory = Path.Combine(dataDirectory, "experiment-recovery-failures");
                    Directory.CreateDirectory(failureDirectory);
                    await WriteTextAtomicallyAsync(
                        Path.Combine(
                            failureDirectory,
                            $"{MakeSafeName(Path.GetFileName(sessionDirectory))}.json"),
                        JsonSerializer.Serialize(new
                        {
                            SessionId = Path.GetFileName(sessionDirectory),
                            FailureKind = ClassifyFailure(ex),
                            DetectedAtUtc = DateTimeOffset.UtcNow
                        }, JsonOptions),
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // One damaged run must not disable new evidence recording.
                }
            }
        }
    }

    private static async Task<ExperimentUploadWorkItem?> ReadUsableBundleCheckpointAsync(
        string sessionDirectory,
        ExperimentUploadSettings fallbackTarget,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = Path.Combine(sessionDirectory, "bundle-checkpoint.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var checkpoint = JsonSerializer.Deserialize<ExperimentBundleCheckpoint>(
                await File.ReadAllTextAsync(path, cancellationToken), JsonOptions);
            if (checkpoint is null ||
                !File.Exists(checkpoint.BundlePath) ||
                new FileInfo(checkpoint.BundlePath).Length != checkpoint.SizeBytes)
            {
                return null;
            }

            var item = await ExperimentUploadWorkItem.CreateAsync(
                checkpoint.SessionId,
                checkpoint.BundlePath,
                cancellationToken,
                sessionDirectory,
                new ExperimentUploadSettings
                {
                    BaseUrl = string.IsNullOrWhiteSpace(checkpoint.TargetBaseUrl)
                        ? fallbackTarget.BaseUrl
                        : checkpoint.TargetBaseUrl,
                    ProjectId = checkpoint.TargetProjectId > 0
                        ? checkpoint.TargetProjectId
                        : fallbackTarget.ProjectId,
                    PackageName = string.IsNullOrWhiteSpace(checkpoint.TargetPackageName)
                        ? fallbackTarget.PackageName
                        : checkpoint.TargetPackageName
                }).ConfigureAwait(false);
            return string.Equals(item.Sha256, checkpoint.Sha256, StringComparison.OrdinalIgnoreCase)
                ? item
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> UpdateRecoveredJsonCheckpointAsync(
        string path,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject;
        }
        catch
        {
            return false;
        }

        if (root is null)
        {
            return false;
        }

        root["LastCheckpointAtUtc"] = recoveredAt;
        root["EndedAtUtc"] = recoveredAt;
        root["FinalizationReason"] = "recovered-after-unclean-exit";
        await WriteTextAtomicallyAsync(
            path, root.ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);
        return IsValidJson(path);
    }

    private static async Task<bool> RebuildRecoveredSummaryAsync(
        string path,
        string sessionDirectory,
        DateTimeOffset startedAt,
        DateTimeOffset recoveredAt,
        CancellationToken cancellationToken)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken)) as JsonObject;
        }
        catch
        {
            return false;
        }

        if (root is null)
        {
            return false;
        }

        var statistics = RebuildStatisticsFromCsv(sessionDirectory, cancellationToken);
        root["LastCheckpointAtUtc"] = recoveredAt;
        root["EndedAtUtc"] = recoveredAt;
        root["DurationSeconds"] = Math.Max(0, (recoveredAt - startedAt).TotalSeconds);
        root["TemperatureSampleCount"] = statistics.TemperatureSampleCount;
        root["ValidTemperatureCount"] = statistics.ValidTemperatureCount;
        root["ValidTemperatureRate"] = statistics.TemperatureSampleCount == 0
            ? null
            : (double)statistics.ValidTemperatureCount / statistics.TemperatureSampleCount;
        root["NoReadingCount"] = statistics.NoReadingCount;
        root["AutomaticTemperatureTripCount"] = statistics.AutomaticTemperatureTripCount;
        root["AutomaticRecoveryCount"] = statistics.AutomaticRecoveryCount;
        root["EngineeringOpenAllCommandCount"] = statistics.EngineeringOpenAllCommandCount;
        root["EngineeringCloseAllCommandCount"] = statistics.EngineeringCloseAllCommandCount;
        root["ManualResetCommandCount"] = statistics.ManualResetCommandCount;
        root["FailedControlCommandCount"] = statistics.FailedControlCommandCount;
        root["BlockedControlCommandCount"] = statistics.BlockedControlCommandCount;
        root["MonitoringLoopFailureCount"] = statistics.MonitoringLoopFailureCount;
        root["MinimumTemperatureC"] = statistics.MinimumTemperatureC;
        root["MaximumTemperatureC"] = statistics.MaximumTemperatureC;
        root["AverageTemperatureC"] = statistics.ValidTemperatureCount == 0
            ? null
            : statistics.TemperatureSum / statistics.ValidTemperatureCount;
        root["EventCount"] = statistics.EventCount;
        root["G2000TelemetryCount"] = statistics.G2000TelemetryCount;
        root["GasFlowSampleCount"] = statistics.GasFlowSampleCount;
        root["FinalizationReason"] = "recovered-after-unclean-exit";
        await WriteTextAtomicallyAsync(
            path, root.ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);
        return IsValidJson(path);
    }

    private static RecoveredCsvStatistics RebuildStatisticsFromCsv(
        string sessionDirectory,
        CancellationToken cancellationToken)
    {
        var statistics = new RecoveredCsvStatistics();
        foreach (var fields in ReadCsvRecords(
                     Path.Combine(sessionDirectory, "temperature-samples.csv"), cancellationToken))
        {
            if (fields.Length < 7)
            {
                continue;
            }

            statistics.TemperatureSampleCount++;
            if (string.Equals(fields[3], nameof(MonitorStatus.NoReading), StringComparison.OrdinalIgnoreCase))
            {
                statistics.NoReadingCount++;
            }
            if (string.Equals(fields[5], nameof(RelayAction.StopSent), StringComparison.OrdinalIgnoreCase))
            {
                statistics.AutomaticTemperatureTripCount++;
            }
            if (string.Equals(fields[5], nameof(RelayAction.ResetSent), StringComparison.OrdinalIgnoreCase))
            {
                statistics.AutomaticRecoveryCount++;
            }
            if (double.TryParse(
                    fields[1],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var temperature))
            {
                statistics.RecordTemperature(temperature);
            }
        }

        foreach (var fields in ReadCsvRecords(
                     Path.Combine(sessionDirectory, "events.csv"), cancellationToken))
        {
            if (fields.Length < 5)
            {
                continue;
            }

            statistics.RecordEvent(fields[1], fields[2], fields[3]);
        }

        statistics.G2000TelemetryCount = ReadCsvRecords(
            Path.Combine(sessionDirectory, "g2000-telemetry.csv"), cancellationToken).Count();
        statistics.GasFlowSampleCount = ReadCsvRecords(
            Path.Combine(sessionDirectory, "gas-flow.csv"), cancellationToken).Count();
        return statistics;
    }

    private static IEnumerable<string[]> ReadCsvRecords(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            yield break;
        }

        using var parser = new TextFieldParser(path, Encoding.UTF8)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        var headerRead = false;
        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[]? fields;
            try
            {
                fields = parser.ReadFields();
            }
            catch (MalformedLineException)
            {
                continue;
            }

            if (!headerRead)
            {
                headerRead = true;
                continue;
            }
            if (fields is not null)
            {
                yield return fields;
            }
        }
    }

    private sealed class RecoveredCsvStatistics
    {
        public int TemperatureSampleCount { get; set; }
        public int ValidTemperatureCount { get; private set; }
        public int NoReadingCount { get; set; }
        public int AutomaticTemperatureTripCount { get; set; }
        public int AutomaticRecoveryCount { get; set; }
        public int EngineeringOpenAllCommandCount { get; private set; }
        public int EngineeringCloseAllCommandCount { get; private set; }
        public int ManualResetCommandCount { get; private set; }
        public int FailedControlCommandCount { get; private set; }
        public int BlockedControlCommandCount { get; private set; }
        public int MonitoringLoopFailureCount { get; private set; }
        public double? MinimumTemperatureC { get; private set; }
        public double? MaximumTemperatureC { get; private set; }
        public double TemperatureSum { get; private set; }
        public int EventCount { get; private set; }
        public int G2000TelemetryCount { get; set; }
        public int GasFlowSampleCount { get; set; }

        public void RecordTemperature(double temperature)
        {
            ValidTemperatureCount++;
            TemperatureSum += temperature;
            MinimumTemperatureC = MinimumTemperatureC.HasValue
                ? Math.Min(MinimumTemperatureC.Value, temperature)
                : temperature;
            MaximumTemperatureC = MaximumTemperatureC.HasValue
                ? Math.Max(MaximumTemperatureC.Value, temperature)
                : temperature;
        }

        public void RecordEvent(string category, string action, string outcome)
        {
            EventCount++;
            var controlCategory = category.Equals("relay", StringComparison.OrdinalIgnoreCase) ||
                                  category.Equals("interlock", StringComparison.OrdinalIgnoreCase) ||
                                  category.Equals("g2000", StringComparison.OrdinalIgnoreCase) ||
                                  category.Equals("amc2100", StringComparison.OrdinalIgnoreCase);
            if (controlCategory && outcome.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                FailedControlCommandCount++;
            }
            if (controlCategory && outcome.Equals("blocked", StringComparison.OrdinalIgnoreCase))
            {
                BlockedControlCommandCount++;
            }
            if (category.Equals("monitoring", StringComparison.OrdinalIgnoreCase) &&
                action.Equals("poll-loop", StringComparison.OrdinalIgnoreCase) &&
                outcome.Equals("failed", StringComparison.OrdinalIgnoreCase))
            {
                MonitoringLoopFailureCount++;
            }
            if (!outcome.Equals("command-completed", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (category.Equals("relay", StringComparison.OrdinalIgnoreCase) &&
                action.Equals("open-all-interlocks", StringComparison.OrdinalIgnoreCase))
            {
                EngineeringOpenAllCommandCount++;
            }
            else if (category.Equals("relay", StringComparison.OrdinalIgnoreCase) &&
                     action.Equals("close-all-interlocks", StringComparison.OrdinalIgnoreCase))
            {
                EngineeringCloseAllCommandCount++;
            }
            else if (category.Equals("interlock", StringComparison.OrdinalIgnoreCase) &&
                     action.Equals("manual-reset", StringComparison.OrdinalIgnoreCase))
            {
                ManualResetCommandCount++;
            }
        }
    }

    private static async Task<LabProfile> LoadRunProfileAsync(
        string sessionDirectory,
        LabProfile fallback,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(sessionDirectory, LabProfileStore.FileName);
        if (File.Exists(path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize<LabProfile>(json, JsonOptions) ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        await WriteTextAtomicallyAsync(
            path,
            LabProfileStore.Serialize(fallback),
            cancellationToken).ConfigureAwait(false);
        return fallback;
    }

    private static async Task<AppSettings> LoadRunSettingsAsync(
        string sessionDirectory,
        AppSettings fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(
                Path.Combine(sessionDirectory, "settings-latest.json"), cancellationToken)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static DateTimeOffset? ReadStartedAt(string manifestPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return document.RootElement.TryGetProperty("StartedAtUtc", out var value) &&
                   value.TryGetDateTimeOffset(out var startedAt)
                ? startedAt
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class ExperimentBundleCheckpoint
    {
        public string SessionId { get; set; } = string.Empty;
        public string BundlePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public bool RecoveredAfterUncleanExit { get; set; }
        public string TargetBaseUrl { get; set; } = string.Empty;
        public int TargetProjectId { get; set; }
        public string TargetPackageName { get; set; } = string.Empty;
        public string TargetCredentialName { get; set; } = string.Empty;
    }

    private static bool HasRecordedActivity(string sessionDirectory)
    {
        var temperaturePath = Path.Combine(sessionDirectory, "temperature-samples.csv");
        if (File.Exists(temperaturePath) && File.ReadLines(temperaturePath).Skip(1).Any())
        {
            return true;
        }

        var eventsPath = Path.Combine(sessionDirectory, "events.csv");
        return File.Exists(eventsPath) && File.ReadLines(eventsPath).Skip(1).Any(line =>
            !line.Contains(",application,session-started,", StringComparison.OrdinalIgnoreCase) &&
            !line.Contains(",application,session-stopped,", StringComparison.OrdinalIgnoreCase));
    }

    private static void CreateRecoveredArchive(string sessionDirectory, string bundlePath)
    {
        var fileNames = new[]
        {
            "manifest.json", "settings-start.json", "settings-latest.json",
            "temperature-samples.csv", "events.csv", "g2000-telemetry.csv",
            "gas-flow.csv", "report-summary.json", "lab-profile.json", "experiment-context.md"
        };
        if (fileNames.Any(fileName => !File.Exists(Path.Combine(sessionDirectory, fileName))))
        {
            return;
        }

        var temporaryPath = $"{bundlePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var destination = File.Create(temporaryPath))
            using (var archive = new ZipArchive(destination, ZipArchiveMode.Create))
            {
                foreach (var fileName in fileNames)
                {
                    archive.CreateEntryFromFile(
                        Path.Combine(sessionDirectory, fileName),
                        fileName,
                        CompressionLevel.Optimal);
                }
            }

            File.Move(temporaryPath, bundlePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static bool HasOutboxArtifact(string dataDirectory, string sessionId)
    {
        var fileName = $"{MakeSafeName(sessionId)}.json";
        return new[] { "pending", "uploading", "sent", "failed" }.Any(state =>
            File.Exists(Path.Combine(dataDirectory, "experiment-upload-outbox", state, fileName)) ||
            Directory.Exists(Path.Combine(dataDirectory, "experiment-upload-outbox", state)) &&
            Directory.GetFiles(
                Path.Combine(dataDirectory, "experiment-upload-outbox", state),
                $"{MakeSafeName(sessionId)}.*.json").Length > 0);
    }

    private static async Task RecoverStaleUploadLeasesAsync(
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        var uploadingDirectory = Path.Combine(dataDirectory, "experiment-upload-outbox", "uploading");
        var pendingDirectory = Path.Combine(dataDirectory, "experiment-upload-outbox", "pending");
        if (!Directory.Exists(uploadingDirectory))
        {
            return;
        }

        foreach (var leasePath in Directory.GetFiles(uploadingDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lockPath = $"{leasePath}.lock";
            try
            {
                using (File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    using (File.Open(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        // Both locks prove that no live uploader owns this lease.
                    }

                    File.Move(
                        leasePath,
                        Path.Combine(pendingDirectory, Path.GetFileName(leasePath)),
                        overwrite: true);
                }
                try { File.Delete(lockPath); } catch { }
            }
            catch (IOException)
            {
                // A concurrently running process still owns the exclusive lease.
            }
        }

        await Task.CompletedTask;
    }

    private sealed class UploadLease : IDisposable
    {
        private int _disposed;
        private FileStream? _dataStream;
        private readonly FileStream _lockStream;

        public UploadLease(string path, string lockPath, FileStream lockStream)
        {
            Path = path;
            LockPath = lockPath;
            _lockStream = lockStream;
        }

        public string Path { get; }
        public string LockPath { get; }
        public FileStream Stream => _dataStream ??= new FileStream(
            Path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            useAsync: true);

        public void CloseDataStream()
        {
            _dataStream?.Dispose();
            _dataStream = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                CloseDataStream();
                _lockStream.Dispose();
                try { File.Delete(LockPath); } catch { }
            }
        }
    }

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath, content, Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static bool IsValidJson(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private abstract record RoutedRecord
    {
        public abstract void Apply(ExperimentSessionRecorder recorder);
    }

    private sealed record RoutedTemperature(TemperatureSample Sample) : RoutedRecord
    {
        public override void Apply(ExperimentSessionRecorder recorder) =>
            recorder.RecordTemperature(Sample);
    }

    private sealed record RoutedEvent(
        DateTimeOffset CapturedAt,
        string Category,
        string Action,
        string Outcome,
        string Details) : RoutedRecord
    {
        public override void Apply(ExperimentSessionRecorder recorder) =>
            recorder.RecordEventAt(CapturedAt, Category, Action, Outcome, Details);
    }

    private sealed record RoutedTelemetry(
        DateTimeOffset CapturedAt,
        G2000TelemetrySnapshot Snapshot) : RoutedRecord
    {
        public override void Apply(ExperimentSessionRecorder recorder) =>
            recorder.RecordG2000TelemetryAt(Snapshot, CapturedAt);
    }

    private sealed record RoutedGasFlow(
        DateTimeOffset CapturedAt,
        double? ActualFlow,
        bool Enabled,
        string Outcome,
        string Details) : RoutedRecord
    {
        public override void Apply(ExperimentSessionRecorder recorder) =>
            recorder.RecordGasFlowAt(CapturedAt, ActualFlow, Enabled, Outcome, Details);
    }

    private sealed record RoutedSettings(AppSettings Settings) : RoutedRecord
    {
        public override void Apply(ExperimentSessionRecorder recorder) =>
            recorder.UpdateSettings(Settings);
    }
}
