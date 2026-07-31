using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ReactorSoftInterlock.Application.G2000;
using ReactorSoftInterlock.Domain;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Logging;

/// <summary>
/// Records report-oriented experiment evidence without participating in the
/// monitoring or interlock decision path.
/// </summary>
public sealed class ExperimentSessionRecorder : IAsyncDisposable
{
    private const string TemperatureFileName = "temperature-samples.csv";
    private const string EventsFileName = "events.csv";
    private const string G2000FileName = "g2000-telemetry.csv";
    private const string GasFlowFileName = "gas-flow.csv";
    private const string SettingsStartFileName = "settings-start.json";
    private const string SettingsLatestFileName = "settings-latest.json";
    private const string ManifestFileName = "manifest.json";
    private const string SummaryFileName = "report-summary.json";
    private const string LabProfileFileName = "lab-profile.json";
    private const string ExperimentContextFileName = "experiment-context.md";
    private const int QueueCapacity = 4096;
    private static readonly TimeSpan G2000PeriodicInterval = TimeSpan.FromSeconds(1);

    private const string TemperatureHeader =
        "timestamp_utc,temperature_c,raw_ocr_text,status,alarm_reason,relay_action,screenshot_roi";
    private const string EventsHeader =
        "timestamp_utc,category,action,outcome,details";
    private const string GasFlowHeader =
        "timestamp_utc,enabled,actual_flow_ml_min,outcome,details";
    private const string G2000Header =
        "timestamp_utc,connected,communication_healthy,last_received_at,last_sent_at,ready,fault,hv_enable,hv_on,source,error_code,error_text," +
        "dc_link_voltage_v,reserved_dc_link_current_a,dc_link_aux_value,reserved_output_voltage_v,reserved_output_current_a," +
        "frequency_khz,duty_percent,ton_ms,toff_ms,status_frame_hex,dc_link_actual_frame_hex,inverter_actual_frame_hex," +
        "reserved_actual_frame_hex,pulse_actual_frame_hex,target_hv_state,ui_mode,automatic_stage,trip_latched,trip_reason," +
        "target_voltage_v,target_frequency_khz,target_duty_percent,target_ton_ms,target_toff_ms," +
        "actual_voltage_v,actual_frequency_khz,actual_duty_percent,actual_ton_ms,actual_toff_ms";

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private static readonly string[] SessionFileNames =
    [
        ManifestFileName,
        SettingsStartFileName,
        SettingsLatestFileName,
        TemperatureFileName,
        EventsFileName,
        G2000FileName,
        GasFlowFileName,
        SummaryFileName,
        LabProfileFileName,
        ExperimentContextFileName
    ];

    private readonly Channel<RecorderCommand> _channel;
    private readonly Task _writerTask;
    private readonly string _softwareVersion;
    private readonly LabProfile _labProfile;
    private readonly RecorderStatistics _statistics = new();
    private readonly object _g2000SamplingGate = new();
    private G2000SignificantState? _lastG2000State;
    private DateTimeOffset _lastG2000RecordedAt = DateTimeOffset.MinValue;
    private long _droppedRecordCount;
    private long _droppedTelemetryCount;
    private long _meaningfulRecordCount;
    private int _accepting = 1;
    private string _latestSettingsJson;
    private string _finalizationReason = string.Empty;

    private ExperimentSessionRecorder(
        string sessionId,
        string sessionDirectory,
        DateTimeOffset startedAt,
        string softwareVersion,
        LabProfile labProfile,
        string settingsJson)
    {
        SessionId = sessionId;
        SessionDirectory = sessionDirectory;
        StartedAt = startedAt;
        _softwareVersion = softwareVersion;
        _labProfile = labProfile;
        _latestSettingsJson = settingsJson;
        _channel = Channel.CreateBounded<RecorderCommand>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _writerTask = RunWriterAsync();
    }

    public string SessionId { get; }

    public string SessionDirectory { get; }

    public DateTimeOffset StartedAt { get; }

    public bool HasActivity => Interlocked.Read(ref _meaningfulRecordCount) > 0;

    public static async Task<ExperimentSessionRecorder> CreateAsync(
        string dataDirectory,
        AppSettings settings,
        string softwareVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(settings);

        var startedAt = DateTimeOffset.UtcNow;
        var sessionId = $"{startedAt:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}"[..30];
        var sessionDirectory = Path.Combine(dataDirectory, "experiment-sessions", sessionId);
        Directory.CreateDirectory(sessionDirectory);

        var labProfile = await LabProfileStore.LoadOrCreateAsync(dataDirectory, cancellationToken)
            .ConfigureAwait(false);
        var settingsJson = Serialize(settings);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, SettingsStartFileName),
            settingsJson,
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, SettingsLatestFileName),
            settingsJson,
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, TemperatureFileName),
            TemperatureHeader + Environment.NewLine,
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, EventsFileName),
            EventsHeader + Environment.NewLine,
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, G2000FileName),
            G2000Header + Environment.NewLine,
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, GasFlowFileName),
            GasFlowHeader + Environment.NewLine,
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, LabProfileFileName),
            LabProfileStore.Serialize(labProfile),
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(sessionDirectory, ExperimentContextFileName),
            LabProfileStore.RenderExperimentContext(
                labProfile,
                settings,
                sessionId,
                startedAt,
                endedAt: null,
                finalizationReason: string.Empty),
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);

        var recorder = new ExperimentSessionRecorder(
            sessionId,
            sessionDirectory,
            startedAt,
            string.IsNullOrWhiteSpace(softwareVersion) ? "unknown" : softwareVersion,
            labProfile,
            settingsJson);

        try
        {
            await recorder.FlushAsync(cancellationToken).ConfigureAwait(false);
            recorder.RecordEvent("application", "session-started", "success", recorder.SessionId);
            return recorder;
        }
        catch
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void RecordTemperature(TemperatureSample sample)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(sample);
            TryEnqueue(new TemperatureCommand(sample), meaningfulActivity: true);
        }
        catch
        {
            // Evidence recording must never break the monitoring/interlock path.
        }
    }

    public void RecordEvent(string category, string action, string outcome = "", string details = "")
        => RecordEventAt(DateTimeOffset.UtcNow, category, action, outcome, details);

    internal void RecordEventAt(
        DateTimeOffset capturedAt,
        string category,
        string action,
        string outcome = "",
        string details = "")
    {
        try
        {
            var meaningfulActivity = IsMeaningfulActivity(category, action);
            TryEnqueue(new EventCommand(
                capturedAt,
                category ?? string.Empty,
                action ?? string.Empty,
                outcome ?? string.Empty,
                details ?? string.Empty), meaningfulActivity: meaningfulActivity);
        }
        catch
        {
            // Evidence recording must never break the control path.
        }
    }

    public void RecordG2000Telemetry(G2000TelemetrySnapshot snapshot)
        => RecordG2000TelemetryAt(snapshot, DateTimeOffset.UtcNow);

    internal void RecordG2000TelemetryAt(
        G2000TelemetrySnapshot snapshot,
        DateTimeOffset capturedAt)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            var clonedSnapshot = snapshot.Clone();
            var significantState = G2000SignificantState.From(clonedSnapshot);

            lock (_g2000SamplingGate)
            {
                var stateChanged = _lastG2000State is null ||
                    !_lastG2000State.Equals(significantState);
                var periodicSampleDue =
                    capturedAt - _lastG2000RecordedAt >= G2000PeriodicInterval;
                if (!stateChanged && !periodicSampleDue)
                {
                    return;
                }

                if (TryEnqueue(
                        new G2000Command(capturedAt, clonedSnapshot),
                        isTelemetry: true))
                {
                    _lastG2000State = significantState;
                    _lastG2000RecordedAt = capturedAt;
                }
            }
        }
        catch
        {
            // Evidence recording must never break the CAN telemetry callback.
        }
    }

    public void RecordGasFlow(
        double? actualFlow,
        bool enabled,
        string outcome,
        string details = "") => RecordGasFlowAt(
            DateTimeOffset.UtcNow, actualFlow, enabled, outcome, details);

    internal void RecordGasFlowAt(
        DateTimeOffset capturedAt,
        double? actualFlow,
        bool enabled,
        string outcome,
        string details = "")
    {
        try
        {
            TryEnqueue(new GasFlowCommand(
                capturedAt,
                enabled,
                actualFlow,
                outcome ?? string.Empty,
                details ?? string.Empty));
        }
        catch
        {
            // Evidence recording must never break the AMC2100 polling path.
        }
    }

    public void UpdateSettings(AppSettings settings)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(settings);
            TryEnqueue(new SettingsCommand(Serialize(settings)));
        }
        catch
        {
            // Settings remain usable even when the evidence snapshot cannot be made.
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _accepting) == 0)
        {
            await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(
            new CheckpointCommand(completion),
            cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ExportAsync(
        string destinationZipPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);
        var fullDestinationPath = Path.GetFullPath(destinationZipPath);
        if (Volatile.Read(ref _accepting) == 0)
        {
            throw new InvalidOperationException("The experiment session recorder is already closed.");
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(
            new ExportCommand(fullDestinationPath, cancellationToken, completion),
            cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task FinalizeAndExportAsync(
        string destinationZipPath,
        string finalizationReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);
        _finalizationReason = string.IsNullOrWhiteSpace(finalizationReason)
            ? "unspecified"
            : finalizationReason.Trim();
        StopAccepting();
        await _writerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        CreateArchive(Path.GetFullPath(destinationZipPath), cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        StopAccepting();
        await _writerTask.ConfigureAwait(false);
    }

    public async Task<bool> CompleteAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        StopAccepting();
        try
        {
            await _writerTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void StopAccepting()
    {
        if (Interlocked.Exchange(ref _accepting, 0) != 0)
        {
            _channel.Writer.TryComplete();
        }
    }

    private bool TryEnqueue(
        RecorderCommand command,
        bool isTelemetry = false,
        bool meaningfulActivity = false)
    {
        if (Volatile.Read(ref _accepting) == 0)
        {
            return false;
        }

        if (_channel.Writer.TryWrite(command))
        {
            if (meaningfulActivity)
            {
                Interlocked.Increment(ref _meaningfulRecordCount);
            }

            return true;
        }

        Interlocked.Increment(ref _droppedRecordCount);
        if (isTelemetry)
        {
            Interlocked.Increment(ref _droppedTelemetryCount);
        }

        return false;
    }

    private async Task RunWriterAsync()
    {
        await foreach (var command in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (command is CheckpointCommand checkpoint)
            {
                try
                {
                    await WriteCheckpointAsync(
                        DateTimeOffset.UtcNow,
                        endedAtUtc: null).ConfigureAwait(false);
                    checkpoint.Completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    _statistics.RecordingFailureCount++;
                    checkpoint.Completion.TrySetException(ex);
                }

                continue;
            }

            if (command is ExportCommand export)
            {
                try
                {
                    await WriteCheckpointAsync(
                        DateTimeOffset.UtcNow,
                        endedAtUtc: null).ConfigureAwait(false);
                    CreateArchive(export.DestinationZipPath, export.CancellationToken);
                    export.Completion.TrySetResult();
                }
                catch (OperationCanceledException)
                {
                    export.Completion.TrySetCanceled(export.CancellationToken);
                }
                catch (Exception ex)
                {
                    _statistics.RecordingFailureCount++;
                    export.Completion.TrySetException(ex);
                }

                continue;
            }

            try
            {
                await ProcessCommandAsync(command).ConfigureAwait(false);
            }
            catch
            {
                _statistics.RecordingFailureCount++;
            }
        }

        try
        {
            var endedAt = DateTimeOffset.UtcNow;
            await WriteCheckpointAsync(endedAt, endedAt).ConfigureAwait(false);
        }
        catch
        {
            _statistics.RecordingFailureCount++;
        }
    }

    private async Task ProcessCommandAsync(RecorderCommand command)
    {
        switch (command)
        {
            case TemperatureCommand temperature:
                await AppendLineAsync(
                    TemperatureFileName,
                    FormatTemperature(temperature.Sample)).ConfigureAwait(false);
                _statistics.RecordTemperature(temperature.Sample);
                break;

            case EventCommand recordedEvent:
                await AppendLineAsync(
                    EventsFileName,
                    JoinCsv(
                        FormatDate(recordedEvent.Timestamp),
                        recordedEvent.Category,
                        recordedEvent.Action,
                        recordedEvent.Outcome,
                        recordedEvent.Details)).ConfigureAwait(false);
                _statistics.RecordEvent(recordedEvent);
                break;

            case G2000Command g2000:
                await AppendLineAsync(
                    G2000FileName,
                    FormatG2000(g2000.Timestamp, g2000.Snapshot)).ConfigureAwait(false);
                _statistics.G2000TelemetryCount++;
                break;

            case GasFlowCommand gas:
                await AppendLineAsync(
                    GasFlowFileName,
                    JoinCsv(
                        FormatDate(gas.Timestamp),
                        FormatBool(gas.Enabled),
                        FormatDouble(gas.ActualFlow),
                        gas.Outcome,
                        gas.Details)).ConfigureAwait(false);
                _statistics.GasFlowSampleCount++;
                break;

            case SettingsCommand settings:
                _latestSettingsJson = settings.Json;
                await WriteTextAtomicallyAsync(
                    Path.Combine(SessionDirectory, SettingsLatestFileName),
                    settings.Json).ConfigureAwait(false);
                break;
        }
    }

    private Task AppendLineAsync(string fileName, string line)
    {
        return File.AppendAllTextAsync(
            Path.Combine(SessionDirectory, fileName),
            line + Environment.NewLine,
            Utf8WithoutBom);
    }

    private async Task WriteCheckpointAsync(
        DateTimeOffset checkpointAt,
        DateTimeOffset? endedAtUtc)
    {
        var summary = new ExperimentReportSummary
        {
            SessionId = SessionId,
            StartedAtUtc = StartedAt,
            LastCheckpointAtUtc = checkpointAt,
            EndedAtUtc = endedAtUtc,
            DurationSeconds = Math.Max(0, (checkpointAt - StartedAt).TotalSeconds),
            TemperatureSampleCount = _statistics.TemperatureSampleCount,
            ValidTemperatureCount = _statistics.ValidTemperatureCount,
            ValidTemperatureRate = _statistics.ValidTemperatureRate,
            NoReadingCount = _statistics.NoReadingCount,
            AutomaticTemperatureTripCount = _statistics.AutomaticTemperatureTripCount,
            AutomaticRecoveryCount = _statistics.AutomaticRecoveryCount,
            EngineeringOpenAllCommandCount = _statistics.EngineeringOpenAllCommandCount,
            EngineeringCloseAllCommandCount = _statistics.EngineeringCloseAllCommandCount,
            ManualResetCommandCount = _statistics.ManualResetCommandCount,
            FailedControlCommandCount = _statistics.FailedControlCommandCount,
            BlockedControlCommandCount = _statistics.BlockedControlCommandCount,
            MonitoringLoopFailureCount = _statistics.MonitoringLoopFailureCount,
            MinimumTemperatureC = _statistics.MinimumTemperatureC,
            MaximumTemperatureC = _statistics.MaximumTemperatureC,
            AverageTemperatureC = _statistics.AverageTemperatureC,
            EventCount = _statistics.EventCount,
            G2000TelemetryCount = _statistics.G2000TelemetryCount,
            GasFlowSampleCount = _statistics.GasFlowSampleCount,
            RecordingFailureCount = _statistics.RecordingFailureCount,
            DroppedRecordCount = Interlocked.Read(ref _droppedRecordCount),
            DroppedTelemetryCount = Interlocked.Read(ref _droppedTelemetryCount)
        };
        var manifest = new ExperimentSessionManifest
        {
            SessionId = SessionId,
            StartedAtUtc = StartedAt,
            LastCheckpointAtUtc = checkpointAt,
            EndedAtUtc = endedAtUtc,
            SoftwareVersion = _softwareVersion,
            OperatingSystem = RuntimeInformation.OSDescription,
            Framework = RuntimeInformation.FrameworkDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            FinalizationReason = endedAtUtc.HasValue ? _finalizationReason : string.Empty
        };
        summary.FinalizationReason = endedAtUtc.HasValue ? _finalizationReason : string.Empty;

        await WriteTextAtomicallyAsync(
            Path.Combine(SessionDirectory, SummaryFileName),
            Serialize(summary)).ConfigureAwait(false);
        await WriteTextAtomicallyAsync(
            Path.Combine(SessionDirectory, ManifestFileName),
            Serialize(manifest)).ConfigureAwait(false);

        AppSettings contextSettings;
        try
        {
            contextSettings = JsonSerializer.Deserialize<AppSettings>(_latestSettingsJson, JsonOptions)
                ?? new AppSettings();
        }
        catch
        {
            contextSettings = new AppSettings();
        }

        await WriteTextAtomicallyAsync(
            Path.Combine(SessionDirectory, ExperimentContextFileName),
            LabProfileStore.RenderExperimentContext(
                _labProfile,
                contextSettings,
                SessionId,
                StartedAt,
                endedAtUtc,
                endedAtUtc.HasValue ? _finalizationReason : string.Empty)).ConfigureAwait(false);
    }

    private void CreateArchive(string fullDestinationPath, CancellationToken cancellationToken)
    {
        foreach (var sessionFileName in SessionFileNames)
        {
            var sessionFilePath = Path.GetFullPath(Path.Combine(SessionDirectory, sessionFileName));
            if (string.Equals(sessionFilePath, fullDestinationPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The export destination cannot overwrite a source file in the experiment session.");
            }
        }

        var destinationDirectory = Path.GetDirectoryName(fullDestinationPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var temporaryPath = Path.Combine(
            destinationDirectory ?? Directory.GetCurrentDirectory(),
            $".{Path.GetFileName(fullDestinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var destination = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.None))
            using (var archive = new ZipArchive(destination, ZipArchiveMode.Create))
            {
                foreach (var fileName in SessionFileNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var filePath = Path.Combine(SessionDirectory, fileName);
                    archive.CreateEntryFromFile(
                        filePath,
                        fileName,
                        CompressionLevel.Optimal);
                }
            }

            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A failed export must not affect an existing evidence bundle.
            }
        }
    }

    private static async Task WriteTextAtomicallyAsync(string path, string content)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                content,
                Utf8WithoutBom).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // A leftover temporary file is ignored and is never included in exports.
            }
        }
    }

    private static string FormatTemperature(TemperatureSample sample)
    {
        return JoinCsv(
            FormatDate(sample.Timestamp),
            FormatDouble(sample.TemperatureC),
            sample.RawOcrText,
            sample.Status.ToString(),
            sample.AlarmReason,
            sample.RelayAction.ToString(),
            sample.ScreenshotRoi);
    }

    private static string FormatG2000(
        DateTimeOffset timestamp,
        G2000TelemetrySnapshot snapshot)
    {
        var target = snapshot.TargetSetpoints ?? new G2000WritableSetpoints();
        var actual = snapshot.ActualSetpoints ?? new G2000WritableSetpoints();
        return JoinCsv(
            FormatDate(timestamp),
            FormatBool(snapshot.Connected),
            FormatBool(snapshot.CommunicationHealthy),
            FormatDate(snapshot.LastReceivedAt),
            FormatDate(snapshot.LastSentAt),
            FormatBool(snapshot.Ready),
            FormatBool(snapshot.Fault),
            FormatBool(snapshot.HvEnable),
            FormatBool(snapshot.HvOn),
            snapshot.Source.ToString(),
            $"0x{snapshot.ErrorCode:X2}",
            snapshot.ErrorText,
            FormatDouble(snapshot.DcLinkVoltageV),
            FormatDouble(snapshot.ReservedDcLinkCurrentA),
            FormatDouble(snapshot.DcLinkAuxValue),
            FormatDouble(snapshot.ReservedOutputVoltageV),
            FormatDouble(snapshot.ReservedOutputCurrentA),
            FormatDouble(snapshot.FrequencyKhz),
            FormatDouble(snapshot.DutyPercent),
            FormatDouble(snapshot.TonMs),
            FormatDouble(snapshot.ToffMs),
            snapshot.StatusFrameHex,
            snapshot.DcLinkActualFrameHex,
            snapshot.InverterActualFrameHex,
            snapshot.ReservedActualFrameHex,
            snapshot.PulseActualFrameHex,
            snapshot.TargetHvState.ToString(),
            snapshot.UiMode.ToString(),
            snapshot.AutomaticStage,
            FormatBool(snapshot.TripLatched),
            snapshot.TripReason,
            FormatDouble(target.VoltageV),
            FormatDouble(target.FrequencyKhz),
            FormatDouble(target.DutyPercent),
            FormatDouble(target.TonMs),
            FormatDouble(target.ToffMs),
            FormatDouble(actual.VoltageV),
            FormatDouble(actual.FrequencyKhz),
            FormatDouble(actual.DutyPercent),
            FormatDouble(actual.TonMs),
            FormatDouble(actual.ToffMs));
    }

    private static string JoinCsv(params string?[] values)
    {
        return string.Join(",", values.Select(EscapeCsv));
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string FormatDate(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value.HasValue ? FormatDate(value.Value) : string.Empty;
    }

    private static string FormatDouble(double? value)
    {
        return value?.ToString("G17", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string FormatBool(bool value)
    {
        return value ? "true" : "false";
    }

    private static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    private static bool IsMeaningfulActivity(string? category, string? action)
    {
        if (string.Equals(category, "application", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(category, "evidence", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(category) || !string.IsNullOrWhiteSpace(action);
    }

    private abstract record RecorderCommand;

    private sealed record TemperatureCommand(TemperatureSample Sample) : RecorderCommand;

    private sealed record EventCommand(
        DateTimeOffset Timestamp,
        string Category,
        string Action,
        string Outcome,
        string Details) : RecorderCommand;

    private sealed record G2000Command(
        DateTimeOffset Timestamp,
        G2000TelemetrySnapshot Snapshot) : RecorderCommand;

    private sealed record GasFlowCommand(
        DateTimeOffset Timestamp,
        bool Enabled,
        double? ActualFlow,
        string Outcome,
        string Details) : RecorderCommand;

    private sealed record SettingsCommand(string Json) : RecorderCommand;

    private sealed record CheckpointCommand(TaskCompletionSource Completion) : RecorderCommand;

    private sealed record ExportCommand(
        string DestinationZipPath,
        CancellationToken CancellationToken,
        TaskCompletionSource Completion) : RecorderCommand;

    private sealed record G2000SignificantState(
        bool Connected,
        bool CommunicationHealthy,
        bool Ready,
        bool Fault,
        bool HvEnable,
        bool HvOn,
        G2000ControlSource Source,
        byte ErrorCode,
        G2000HvState TargetHvState,
        G2000UiMode UiMode,
        string AutomaticStage,
        bool TripLatched,
        string TripReason,
        double TargetVoltageV,
        double TargetFrequencyKhz,
        double TargetDutyPercent,
        double TargetTonMs,
        double TargetToffMs)
    {
        public static G2000SignificantState From(G2000TelemetrySnapshot snapshot)
        {
            var target = snapshot.TargetSetpoints ?? new G2000WritableSetpoints();
            return new G2000SignificantState(
                snapshot.Connected,
                snapshot.CommunicationHealthy,
                snapshot.Ready,
                snapshot.Fault,
                snapshot.HvEnable,
                snapshot.HvOn,
                snapshot.Source,
                snapshot.ErrorCode,
                snapshot.TargetHvState,
                snapshot.UiMode,
                snapshot.AutomaticStage,
                snapshot.TripLatched,
                snapshot.TripReason,
                target.VoltageV,
                target.FrequencyKhz,
                target.DutyPercent,
                target.TonMs,
                target.ToffMs);
        }
    }

    private sealed class RecorderStatistics
    {
        private double _temperatureSum;

        public int TemperatureSampleCount { get; private set; }

        public int ValidTemperatureCount { get; private set; }

        public int NoReadingCount { get; private set; }

        public int AutomaticTemperatureTripCount { get; private set; }

        public int AutomaticRecoveryCount { get; private set; }

        public int EngineeringOpenAllCommandCount { get; private set; }

        public int EngineeringCloseAllCommandCount { get; private set; }

        public int ManualResetCommandCount { get; private set; }

        public int FailedControlCommandCount { get; private set; }

        public int BlockedControlCommandCount { get; private set; }

        public int MonitoringLoopFailureCount { get; private set; }

        public double? MinimumTemperatureC { get; private set; }

        public double? MaximumTemperatureC { get; private set; }

        public double? AverageTemperatureC =>
            ValidTemperatureCount == 0 ? null : _temperatureSum / ValidTemperatureCount;

        public double? ValidTemperatureRate =>
            TemperatureSampleCount == 0
                ? null
                : (double)ValidTemperatureCount / TemperatureSampleCount;

        public int EventCount { get; set; }

        public int G2000TelemetryCount { get; set; }

        public int GasFlowSampleCount { get; set; }

        public int RecordingFailureCount { get; set; }

        public void RecordEvent(EventCommand recordedEvent)
        {
            EventCount++;
            if (string.Equals(recordedEvent.Outcome, "failed", StringComparison.OrdinalIgnoreCase) &&
                IsControlCategory(recordedEvent.Category))
            {
                FailedControlCommandCount++;
            }

            if (string.Equals(recordedEvent.Outcome, "blocked", StringComparison.OrdinalIgnoreCase) &&
                IsControlCategory(recordedEvent.Category))
            {
                BlockedControlCommandCount++;
            }

            if (string.Equals(recordedEvent.Category, "monitoring", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(recordedEvent.Action, "poll-loop", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(recordedEvent.Outcome, "failed", StringComparison.OrdinalIgnoreCase))
            {
                MonitoringLoopFailureCount++;
            }

            if (string.Equals(recordedEvent.Outcome, "command-completed", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(recordedEvent.Category, "relay", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(recordedEvent.Action, "open-all-interlocks", StringComparison.OrdinalIgnoreCase))
                {
                    EngineeringOpenAllCommandCount++;
                }
                else if (string.Equals(recordedEvent.Category, "relay", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(recordedEvent.Action, "close-all-interlocks", StringComparison.OrdinalIgnoreCase))
                {
                    EngineeringCloseAllCommandCount++;
                }
                else if (string.Equals(recordedEvent.Category, "interlock", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(recordedEvent.Action, "manual-reset", StringComparison.OrdinalIgnoreCase))
                {
                    ManualResetCommandCount++;
                }
            }
        }

        public void RecordTemperature(TemperatureSample sample)
        {
            TemperatureSampleCount++;
            if (sample.Status == MonitorStatus.NoReading)
            {
                NoReadingCount++;
            }

            if (sample.RelayAction == RelayAction.StopSent)
            {
                AutomaticTemperatureTripCount++;
            }

            if (sample.RelayAction == RelayAction.ResetSent)
            {
                AutomaticRecoveryCount++;
            }

            if (!sample.TemperatureC.HasValue)
            {
                return;
            }

            var temperature = sample.TemperatureC.Value;
            ValidTemperatureCount++;
            _temperatureSum += temperature;
            MinimumTemperatureC = !MinimumTemperatureC.HasValue
                ? temperature
                : Math.Min(MinimumTemperatureC.Value, temperature);
            MaximumTemperatureC = !MaximumTemperatureC.HasValue
                ? temperature
                : Math.Max(MaximumTemperatureC.Value, temperature);
        }

        private static bool IsControlCategory(string category)
        {
            return category.Equals("relay", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("interlock", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("g2000", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("amc2100", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class ExperimentSessionManifest
    {
        public string SessionId { get; set; } = string.Empty;

        public DateTimeOffset StartedAtUtc { get; set; }

        public DateTimeOffset LastCheckpointAtUtc { get; set; }

        public DateTimeOffset? EndedAtUtc { get; set; }

        public string SoftwareVersion { get; set; } = string.Empty;

        public string OperatingSystem { get; set; } = string.Empty;

        public string Framework { get; set; } = string.Empty;

        public string ProcessArchitecture { get; set; } = string.Empty;

        public string FinalizationReason { get; set; } = string.Empty;
    }

    private sealed class ExperimentReportSummary
    {
        public string SessionId { get; set; } = string.Empty;

        public DateTimeOffset StartedAtUtc { get; set; }

        public DateTimeOffset LastCheckpointAtUtc { get; set; }

        public DateTimeOffset? EndedAtUtc { get; set; }

        public double DurationSeconds { get; set; }

        public int TemperatureSampleCount { get; set; }

        public int ValidTemperatureCount { get; set; }

        public double? ValidTemperatureRate { get; set; }

        public int NoReadingCount { get; set; }

        public int AutomaticTemperatureTripCount { get; set; }

        public int AutomaticRecoveryCount { get; set; }

        public int EngineeringOpenAllCommandCount { get; set; }

        public int EngineeringCloseAllCommandCount { get; set; }

        public int ManualResetCommandCount { get; set; }

        public int FailedControlCommandCount { get; set; }

        public int BlockedControlCommandCount { get; set; }

        public int MonitoringLoopFailureCount { get; set; }

        public double? MinimumTemperatureC { get; set; }

        public double? MaximumTemperatureC { get; set; }

        public double? AverageTemperatureC { get; set; }

        public int EventCount { get; set; }

        public int G2000TelemetryCount { get; set; }

        public int GasFlowSampleCount { get; set; }

        public int RecordingFailureCount { get; set; }

        public long DroppedRecordCount { get; set; }

        public long DroppedTelemetryCount { get; set; }

        public string FinalizationReason { get; set; } = string.Empty;
    }
}
