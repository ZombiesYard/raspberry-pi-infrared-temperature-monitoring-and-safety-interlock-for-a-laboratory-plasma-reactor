using ReactorSoftInterlock.Application.G2000;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Relay;

public sealed class G2000CanController : IG2000Controller, IG2000TripLatch, IG2000RecoveryPreparation
{
    private readonly G2000CanSettings _settings;
    private readonly IPcanBus _pcanBus;
    private readonly Func<DateTimeOffset> _now;
    private readonly bool _startBackgroundLoops;
    private readonly ushort _channelHandle;
    private readonly TimeSpan _commandPeriod;
    private readonly TimeSpan _readPollInterval;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly G2000WritableSetpoints _targetSetpoints;
    private G2000StartupRecipe _startupRecipe;
    private readonly G2000TelemetrySnapshot _snapshot = new();
    private CancellationTokenSource? _loopsCts;
    private Task? _senderTask;
    private Task? _readerTask;
    private G2000HvState _targetHvState = G2000HvState.HvAus;
    private G2000UiMode _uiMode;
    private TripRecoveryPolicy _recoveryPolicy;
    private bool _setpointsDirty = true;
    private bool _initialized;
    private bool _disposed;
    private G2000AutomaticSequenceState? _automaticSequence;
    private G2000PreTripState? _preTripState;
    private bool _tripLatched;
    private string _tripReason = string.Empty;
    private bool _autoRecoverAfterFaultClear;
    private string _automaticStageLabel = "Idle";
    private DateTimeOffset? _pendingHvOnAt;
    private G2000StartupRecipe? _preparedRecoveryRecipe;
    private G2000HvState? _preparedRecoveryHvState;
    private bool _recoveryPreparationPending;

    public G2000CanController(G2000CanSettings settings)
        : this(settings, new NativePcanBus(), static () => DateTimeOffset.Now, startBackgroundLoops: true)
    {
    }

    internal G2000CanController(
        G2000CanSettings settings,
        IPcanBus pcanBus,
        Func<DateTimeOffset> now,
        bool startBackgroundLoops)
    {
        _settings = settings;
        _pcanBus = pcanBus;
        _now = now;
        _startBackgroundLoops = startBackgroundLoops;
        _settings.Normalize();
        _channelHandle = PcanChannelParser.ParseOrThrow(settings.Channel);
        if (settings.CommandPeriodMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.CommandPeriodMs), settings.CommandPeriodMs, "G2000 CAN command period must be a positive number of milliseconds.");
        }

        if (settings.NodeId > 0x7E)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.NodeId), settings.NodeId, "G2000 CAN node IDs must be in the range 0x00..0x7E.");
        }

        _commandPeriod = TimeSpan.FromMilliseconds(settings.CommandPeriodMs);
        _readPollInterval = TimeSpan.FromMilliseconds(settings.ReadPollIntervalMs);
        _targetSetpoints = settings.WritableSetpoints.Clone();
        _startupRecipe = settings.StartupRecipe.Clone();
        _recoveryPolicy = settings.ResolveRecoveryPolicy();
        _uiMode = settings.ResolveUiMode();
        UpdateSnapshotLocked();
    }

    public event EventHandler<G2000TelemetrySnapshot>? TelemetryUpdated;

    public G2000TelemetrySnapshot Snapshot
    {
        get
        {
            lock (_sync)
            {
                return _snapshot.Clone();
            }
        }
    }

    public G2000WritableSetpoints TargetSetpoints
    {
        get
        {
            lock (_sync)
            {
                return _targetSetpoints.Clone();
            }
        }
    }

    public G2000StartupRecipe StartupRecipe
    {
        get
        {
            lock (_sync)
            {
                return _startupRecipe.Clone();
            }
        }
    }

    public TripRecoveryPolicy RecoveryPolicy
    {
        get
        {
            lock (_sync)
            {
                return _recoveryPolicy;
            }
        }
        set
        {
            lock (_sync)
            {
                _recoveryPolicy = value;
                UpdateSnapshotLocked();
            }

            PublishTelemetry();
        }
    }

    public G2000UiMode UiMode
    {
        get
        {
            lock (_sync)
            {
                return _uiMode;
            }
        }
        set
        {
            lock (_sync)
            {
                _uiMode = value;
                UpdateSnapshotLocked();
            }

            PublishTelemetry();
        }
    }

    public bool IsTripLatched
    {
        get
        {
            lock (_sync)
            {
                return _tripLatched;
            }
        }
    }

    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();
        EnsureLoops();
        await SendCurrentStateAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task RunSenderTickForTestAsync(CancellationToken cancellationToken)
    {
        await MaybeAdvanceAutomaticSequenceAsync(cancellationToken).ConfigureAwait(false);
        await SendCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        UpdateCommunicationHealth();
    }

    internal Task HandleIncomingMessageForTestAsync(uint id, byte[] data, CancellationToken cancellationToken)
    {
        return HandleIncomingMessageAsync(id, data, cancellationToken);
    }

    public async Task<RelayAction> StopAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            LatchTripLocked("Temperature limit trip", autoRecoverAfterFaultClear: false);
        }

        await SetHvStateInternalAsync(G2000HvState.HvAus, cancellationToken, switchToManualMode: false, clearTrip: false).ConfigureAwait(false);
        return RelayAction.StopSent;
    }

    public async Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (IsTripLatched)
        {
            await RecoverFromTripAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (IsAutomaticSequenceActive())
        {
            return RelayAction.ResetSent;
        }
        else
        {
            await SetHvStateInternalAsync(G2000HvState.HvReady, cancellationToken, switchToManualMode: true, clearTrip: true).ConfigureAwait(false);
        }

        return RelayAction.ResetSent;
    }

    public void LatchSoftwareTrip(string reason)
    {
        lock (_sync)
        {
            LatchTripLocked(reason, autoRecoverAfterFaultClear: false, forceHvAus: false);
        }

        PublishTelemetry();
    }

    public async Task PrepareRecoveryWhileInterlockOpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();
        EnsureLoops();

        lock (_sync)
        {
            if (!_tripLatched)
            {
                return;
            }

            var decision = G2000RecoveryPlanner.Plan(_recoveryPolicy, _preTripState);
            ApplyRecoveryDecisionLocked(decision, deferAutomaticStart: true);
        }

        await SendCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        PublishTelemetry();
    }

    public async Task CompletePreparedRecoveryAfterInterlockClosedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var shouldSend = false;
        lock (_sync)
        {
            if (_preparedRecoveryRecipe is not null)
            {
                var recipe = _preparedRecoveryRecipe.Clone();
                _preparedRecoveryRecipe = null;
                _preparedRecoveryHvState = null;
                StartAutomaticSequenceLocked(recipe, _now(), clearTrip: true, forceHvReadyBeforeHvOn: true);
                shouldSend = true;
            }
            else if (_preparedRecoveryHvState is { } hvState)
            {
                _preparedRecoveryHvState = null;
                _tripLatched = false;
                _tripReason = string.Empty;
                _autoRecoverAfterFaultClear = false;
                _recoveryPreparationPending = false;
                _targetHvState = hvState;
                _automaticStageLabel = "Manual";
                UpdateSnapshotLocked();
                shouldSend = true;
            }
            else if (_recoveryPreparationPending)
            {
                _tripLatched = false;
                _tripReason = string.Empty;
                _autoRecoverAfterFaultClear = false;
                _recoveryPreparationPending = false;
                UpdateSnapshotLocked();
                shouldSend = true;
            }
        }

        if (shouldSend)
        {
            await SendCurrentStateAsync(cancellationToken).ConfigureAwait(false);
            PublishTelemetry();
        }
    }

    public async Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await SetHvStateInternalAsync(G2000HvState.HvAus, cancellationToken, switchToManualMode: true, clearTrip: true).ConfigureAwait(false);
        return RelayAction.TestStopSent;
    }

    public async Task OpenAllInterlocksAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await SetHvStateInternalAsync(G2000HvState.HvAus, cancellationToken, switchToManualMode: true, clearTrip: true).ConfigureAwait(false);
    }

    public async Task CloseAllInterlocksAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await SetHvStateInternalAsync(G2000HvState.HvReady, cancellationToken, switchToManualMode: true, clearTrip: true).ConfigureAwait(false);
    }

    public Task SetChannelClosedAsync(int channelNumber, bool closed, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("G2000 CAN mode does not support per-channel relay bank control.");
    }

    public async Task SetHvStateAsync(G2000HvState state, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        await SetHvStateInternalAsync(state, cancellationToken, switchToManualMode: true, clearTrip: true).ConfigureAwait(false);
    }

    public async Task ApplyWritableSetpointsAsync(G2000WritableSetpoints setpoints, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setpoints);
        _settings.ValidateWritableSetpoints(setpoints);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            _targetSetpoints.VoltageV = setpoints.VoltageV;
            _targetSetpoints.FrequencyKhz = setpoints.FrequencyKhz;
            _targetSetpoints.DutyPercent = setpoints.DutyPercent;
            _targetSetpoints.TonMs = setpoints.TonMs;
            _targetSetpoints.ToffMs = setpoints.ToffMs;
            _setpointsDirty = true;
            UpdateSnapshotLocked();
        }

        await SendCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        PublishTelemetry();
    }

    public async Task StartAutomaticSequenceAsync(G2000StartupRecipe recipe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        _settings.ValidateStartupRecipe(recipe);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            var now = _now();
            StartAutomaticSequenceLocked(recipe, now, clearTrip: true);
        }

        await SendCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        PublishTelemetry();
    }

    public async Task StopAutomaticSequenceAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _automaticSequence = null;
            _pendingHvOnAt = null;
            _preparedRecoveryRecipe = null;
            _preparedRecoveryHvState = null;
            _recoveryPreparationPending = false;
            _targetHvState = G2000HvState.HvAus;
            _uiMode = G2000UiMode.Manual;
            _automaticStageLabel = "Stopped";
            UpdateSnapshotLocked();
        }

        await SendCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        PublishTelemetry();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _loopsCts?.Cancel();
        try
        {
            var tasks = new[] { _senderTask, _readerTask }
                .Where(static task => task is not null)
                .Cast<Task>()
                .ToArray();
            if (tasks.Length > 0)
            {
                Task.WaitAll(tasks, TimeSpan.FromSeconds(1));
            }
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is TaskCanceledException or OperationCanceledException))
        {
            // Expected during shutdown.
        }
        finally
        {
            _loopsCts?.Dispose();
            _writeGate.Dispose();
        }

        if (_initialized)
        {
            _pcanBus.Uninitialize(_channelHandle);
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        var status = _pcanBus.Initialize(_channelHandle, PcanBasicNative.PcanBaud125K, 0, 0, 0);
        if (status != PcanBasicNative.PcanErrorOk)
        {
            throw new InvalidOperationException($"PCAN initialization failed with status 0x{status:X} on channel {_settings.Channel}.");
        }

        _initialized = true;
        lock (_sync)
        {
            _snapshot.Connected = true;
            UpdateSnapshotLocked();
        }
    }

    private void EnsureLoops()
    {
        lock (_sync)
        {
            if (!_startBackgroundLoops)
            {
                return;
            }

            if (_loopsCts is not null)
            {
                return;
            }

            _loopsCts = new CancellationTokenSource();
            _senderTask = Task.Run(() => RunSenderLoopAsync(_loopsCts.Token));
            _readerTask = Task.Run(() => RunReaderLoopAsync(_loopsCts.Token));
        }
    }

    private async Task RunSenderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_commandPeriod);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await MaybeAdvanceAutomaticSequenceAsync(cancellationToken).ConfigureAwait(false);
                await SendCurrentStateAsync(cancellationToken).ConfigureAwait(false);
                UpdateCommunicationHealth();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunReaderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_readPollInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await DrainReceiveQueueAsync(cancellationToken).ConfigureAwait(false);
                UpdateCommunicationHealth();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task DrainReceiveQueueAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var message = PcanBasicNative.TPCANMsg.CreateEmpty();
            var status = _pcanBus.Read(_channelHandle, ref message, out _);
            if (status == PcanBasicNative.PcanErrorReceiveQueueEmpty)
            {
                return;
            }

            if (status != PcanBasicNative.PcanErrorOk)
            {
                throw new InvalidOperationException($"PCAN read failed with status 0x{status:X} on channel {_settings.Channel}.");
            }

            await HandleIncomingMessageAsync(message.ID, message.DATA, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleIncomingMessageAsync(uint id, byte[] data, CancellationToken cancellationToken)
    {
        var shouldForceStop = false;
        var shouldAutoRecover = false;
        var now = _now();
        lock (_sync)
        {
            _snapshot.LastReceivedAt = now;
            _snapshot.CommunicationHealthy = true;

            switch (id)
            {
                case var statusId when statusId == G2000CanProtocol.GetStatusId(_settings.NodeId):
                {
                    var status = G2000CanProtocol.ParseStatusData(data);
                    _snapshot.Ready = status.Ready;
                    _snapshot.Fault = status.Fault;
                    _snapshot.HvEnable = status.HvEnable;
                    _snapshot.HvOn = status.HvOn;
                    _snapshot.Source = status.Source;
                    _snapshot.ErrorCode = status.ErrorCode;
                    _snapshot.ErrorText = status.ErrorText;
                    _snapshot.StatusFrameHex = G2000CanProtocol.FormatFrame(data);

                    if (status.Fault &&
                        status.ErrorCode is G2000CanProtocol.ErrorCodeInterlock or G2000CanProtocol.ErrorCodeExternalCanTimeout &&
                        !_tripLatched &&
                        _targetHvState != G2000HvState.HvAus)
                    {
                        LatchTripLocked(status.ErrorText, autoRecoverAfterFaultClear: true);
                        shouldForceStop = true;
                    }
                    else if (!status.Fault &&
                             _tripLatched &&
                             _autoRecoverAfterFaultClear &&
                             _recoveryPolicy != TripRecoveryPolicy.HoldHvAus)
                    {
                        shouldAutoRecover = true;
                    }

                    break;
                }
                case var dcLinkId when dcLinkId == G2000CanProtocol.GetDcLinkActualId(_settings.NodeId):
                {
                    var (voltage, current) = G2000CanProtocol.ParseTwoFloatFrame(data);
                    _snapshot.DcLinkVoltageV = voltage;
                    _snapshot.ReservedDcLinkCurrentA = current;
                    _snapshot.DcLinkAuxValue = current;
                    _snapshot.DcLinkActualFrameHex = G2000CanProtocol.FormatFrame(data);
                    _snapshot.ActualSetpoints.VoltageV = voltage;
                    break;
                }
                case var inverterId when inverterId == G2000CanProtocol.GetInverterActualId(_settings.NodeId):
                {
                    var (frequency, duty) = G2000CanProtocol.ParseTwoFloatFrame(data);
                    _snapshot.FrequencyKhz = frequency;
                    _snapshot.DutyPercent = duty;
                    _snapshot.InverterActualFrameHex = G2000CanProtocol.FormatFrame(data);
                    _snapshot.ActualSetpoints.FrequencyKhz = frequency;
                    _snapshot.ActualSetpoints.DutyPercent = duty;
                    break;
                }
                case var reservedId when reservedId == G2000CanProtocol.GetReservedActualId(_settings.NodeId):
                {
                    var (voltage, current) = G2000CanProtocol.ParseTwoFloatFrame(data);
                    _snapshot.ReservedOutputVoltageV = voltage;
                    _snapshot.ReservedOutputCurrentA = current;
                    _snapshot.ReservedActualFrameHex = G2000CanProtocol.FormatFrame(data);
                    break;
                }
                case var pulseId when pulseId == G2000CanProtocol.GetPulseActualId(_settings.NodeId):
                {
                    var (ton, toff) = G2000CanProtocol.ParseTwoFloatFrame(data);
                    _snapshot.TonMs = ton;
                    _snapshot.ToffMs = toff;
                    _snapshot.PulseActualFrameHex = G2000CanProtocol.FormatFrame(data);
                    _snapshot.ActualSetpoints.TonMs = ton;
                    _snapshot.ActualSetpoints.ToffMs = toff;
                    break;
                }
                default:
                    break;
            }

            UpdateSnapshotLocked();
        }

        PublishTelemetry();

        if (shouldForceStop)
        {
            await SetHvStateInternalAsync(G2000HvState.HvAus, cancellationToken, switchToManualMode: false, clearTrip: false).ConfigureAwait(false);
        }
        else if (shouldAutoRecover)
        {
            await RecoverFromTripAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MaybeAdvanceAutomaticSequenceAsync(CancellationToken cancellationToken)
    {
        var shouldSend = false;
        lock (_sync)
        {
            var now = _now();
            if (_pendingHvOnAt is not null)
            {
                if (now < _pendingHvOnAt.Value)
                {
                    return;
                }

                _targetHvState = G2000HvState.HvOn;
                _pendingHvOnAt = null;
                if (_uiMode == G2000UiMode.Automatic)
                {
                    _automaticSequence = new G2000AutomaticSequenceState(_startupRecipe, now);
                    _automaticStageLabel = _automaticSequence.CurrentStage;
                }
                else
                {
                    _automaticStageLabel = "Manual";
                }

                UpdateSnapshotLocked();
                shouldSend = true;
            }

            if (_automaticSequence is null)
            {
                if (!shouldSend)
                {
                    return;
                }
            }

            if (_automaticSequence is not null &&
                _automaticSequence.TryAdvance(now, out var nextVoltageV))
            {
                _targetSetpoints.VoltageV = nextVoltageV;
                _setpointsDirty = true;
                _automaticStageLabel = _automaticSequence.CurrentStage;
                if (_automaticSequence.IsComplete)
                {
                    _automaticSequence.MarkComplete();
                    _automaticStageLabel = "Stage2";
                }

                UpdateSnapshotLocked();
                shouldSend = true;
            }
        }

        if (shouldSend)
        {
            await SendCurrentStateAsync(cancellationToken).ConfigureAwait(false);
            PublishTelemetry();
        }
    }

    private async Task RecoverFromTripAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            var decision = G2000RecoveryPlanner.Plan(_recoveryPolicy, _preTripState);
            ApplyRecoveryDecisionLocked(decision, deferAutomaticStart: false);
        }

        await SendCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        PublishTelemetry();
    }

    private void ApplyRecoveryDecisionLocked(G2000RecoveryDecision decision, bool deferAutomaticStart)
    {
        if (decision.ResumeAutomaticSequence)
        {
            _targetSetpoints.FrequencyKhz = decision.Setpoints.FrequencyKhz;
            _targetSetpoints.DutyPercent = decision.Setpoints.DutyPercent;
            _targetSetpoints.TonMs = decision.Setpoints.TonMs;
            _targetSetpoints.ToffMs = decision.Setpoints.ToffMs;
            if (deferAutomaticStart)
            {
                _autoRecoverAfterFaultClear = false;
                _automaticSequence = null;
                _pendingHvOnAt = null;
                _preparedRecoveryRecipe = decision.Recipe.Clone();
                _preparedRecoveryHvState = null;
                _recoveryPreparationPending = true;
                _startupRecipe = decision.Recipe.Clone();
                _uiMode = G2000UiMode.Automatic;
                _targetHvState = G2000HvState.HvReady;
                _targetSetpoints.VoltageV = _startupRecipe.Stage1VoltageV;
                _setpointsDirty = true;
                _automaticStageLabel = "ReadyLead";
                UpdateSnapshotLocked();
            }
            else
            {
                _preparedRecoveryRecipe = null;
                _preparedRecoveryHvState = null;
                _recoveryPreparationPending = false;
                StartAutomaticSequenceLocked(decision.Recipe, _now(), clearTrip: true, forceHvReadyBeforeHvOn: true);
            }

            return;
        }

        if (!deferAutomaticStart)
        {
            _tripLatched = false;
            _tripReason = string.Empty;
        }

        _autoRecoverAfterFaultClear = false;
        _automaticSequence = null;
        _pendingHvOnAt = null;
        _preparedRecoveryRecipe = null;
        _recoveryPreparationPending = deferAutomaticStart;
        _preparedRecoveryHvState = deferAutomaticStart && decision.HvState == G2000HvState.HvOn
            ? G2000HvState.HvOn
            : null;
        _uiMode = decision.UiMode;
        _targetHvState = _preparedRecoveryHvState is null ? decision.HvState : G2000HvState.HvReady;
        _targetSetpoints.VoltageV = decision.Setpoints.VoltageV;
        _targetSetpoints.FrequencyKhz = decision.Setpoints.FrequencyKhz;
        _targetSetpoints.DutyPercent = decision.Setpoints.DutyPercent;
        _targetSetpoints.TonMs = decision.Setpoints.TonMs;
        _targetSetpoints.ToffMs = decision.Setpoints.ToffMs;
        _startupRecipe = decision.Recipe.Clone();
        _setpointsDirty = true;
        _automaticStageLabel = _preparedRecoveryHvState is null ? decision.AutomaticStage : "PreparedRecovery";
        UpdateSnapshotLocked();
    }

    private void StartAutomaticSequenceLocked(
        G2000StartupRecipe recipe,
        DateTimeOffset now,
        bool clearTrip,
        bool forceHvReadyBeforeHvOn = false)
    {
        _startupRecipe = recipe.Clone();
        _uiMode = G2000UiMode.Automatic;
        _preparedRecoveryRecipe = null;
        _preparedRecoveryHvState = null;
        _recoveryPreparationPending = false;
        if (clearTrip)
        {
            _tripLatched = false;
            _tripReason = string.Empty;
        }

        _autoRecoverAfterFaultClear = false;
        _automaticStageLabel = "Stage1";
        _targetSetpoints.VoltageV = _startupRecipe.Stage1VoltageV;
        _setpointsDirty = true;
        _pendingHvOnAt = null;
        var enterHvReadyBeforeRun = _startupRecipe.EnterHvReadyBeforeRun || forceHvReadyBeforeHvOn;
        if (enterHvReadyBeforeRun)
        {
            _targetHvState = G2000HvState.HvReady;
            if (_startupRecipe.EnterHvOnAtStart)
            {
                _automaticSequence = null;
                _pendingHvOnAt = now.AddMilliseconds(_settings.HvReadyLeadTimeMs);
                _automaticStageLabel = "ReadyLead";
            }
            else
            {
                _automaticSequence = new G2000AutomaticSequenceState(_startupRecipe, now);
            }
        }
        else if (_startupRecipe.EnterHvOnAtStart)
        {
            _targetHvState = G2000HvState.HvOn;
            _automaticSequence = new G2000AutomaticSequenceState(_startupRecipe, now);
        }
        else
        {
            _automaticSequence = new G2000AutomaticSequenceState(_startupRecipe, now);
        }

        UpdateSnapshotLocked();
    }

    private async Task SetHvStateInternalAsync(G2000HvState state, CancellationToken cancellationToken, bool switchToManualMode, bool clearTrip)
    {
        lock (_sync)
        {
            _pendingHvOnAt = null;
            _preparedRecoveryRecipe = null;
            _preparedRecoveryHvState = null;
            _recoveryPreparationPending = false;
            if (state == G2000HvState.HvOn &&
                !_snapshot.HvEnable &&
                _settings.HvReadyLeadTimeMs > 0)
            {
                _targetHvState = G2000HvState.HvReady;
                _pendingHvOnAt = _now().AddMilliseconds(_settings.HvReadyLeadTimeMs);
                _automaticStageLabel = "ReadyLead";
            }
            else
            {
                _targetHvState = state;
            }

            _automaticSequence = null;
            if (_pendingHvOnAt is null)
            {
                _automaticStageLabel = "Manual";
            }

            if (switchToManualMode)
            {
                _uiMode = G2000UiMode.Manual;
            }

            if (clearTrip)
            {
                _tripLatched = false;
                _tripReason = string.Empty;
                _autoRecoverAfterFaultClear = false;
            }

            UpdateSnapshotLocked();
        }

        await SendCurrentStateAsync(cancellationToken).ConfigureAwait(false);
        PublishTelemetry();
    }

    private async Task SendCurrentStateAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] commandData;
            byte[]? dcLinkData = null;
            byte[]? inverterData = null;
            byte[]? pulseData = null;
            lock (_sync)
            {
                commandData = BuildCommandDataLocked();
                if (_setpointsDirty)
                {
                    dcLinkData = G2000CanProtocol.CreateDcLinkSetpointData(_targetSetpoints.VoltageV);
                    inverterData = G2000CanProtocol.CreateInverterSetpointData(_targetSetpoints.FrequencyKhz, _targetSetpoints.DutyPercent);
                    pulseData = G2000CanProtocol.CreatePulseSetpointData(_targetSetpoints.TonMs, _targetSetpoints.ToffMs);
                    _setpointsDirty = false;
                }
            }

            if (dcLinkData is not null)
            {
                await WriteFrameAsync(G2000CanProtocol.GetDcLinkSetpointId(_settings.NodeId), dcLinkData, cancellationToken).ConfigureAwait(false);
                await WriteFrameAsync(G2000CanProtocol.GetInverterSetpointId(_settings.NodeId), inverterData!, cancellationToken).ConfigureAwait(false);
                await WriteFrameAsync(G2000CanProtocol.GetPulseSetpointId(_settings.NodeId), pulseData!, cancellationToken).ConfigureAwait(false);
            }

            await WriteFrameAsync(G2000CanProtocol.GetCommandId(_settings.NodeId), commandData, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                _snapshot.LastSentAt = _now();
                UpdateSnapshotLocked();
            }
        }
        finally
        {
            _writeGate.Release();
        }

        PublishTelemetry();
    }

    private Task WriteFrameAsync(uint id, byte[] data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = PcanBasicNative.TPCANMsg.CreateStandard(id, data);
        var status = _pcanBus.Write(_channelHandle, ref message);
        if (status != PcanBasicNative.PcanErrorOk)
        {
            throw new InvalidOperationException($"PCAN write failed with status 0x{status:X} on channel {_settings.Channel}.");
        }

        return Task.CompletedTask;
    }

    private byte[] BuildCommandDataLocked()
    {
        return _targetHvState switch
        {
            G2000HvState.HvOn => G2000CanProtocol.CreateCanBusHvOnData(),
            G2000HvState.HvReady => G2000CanProtocol.CreateCanBusHvReadyData(),
            _ => G2000CanProtocol.CreateCanBusStopData()
        };
    }

    private bool IsAutomaticSequenceActive()
    {
        lock (_sync)
        {
            return _uiMode == G2000UiMode.Automatic &&
                   (_automaticSequence is not null ||
                    _pendingHvOnAt is not null ||
                    _automaticStageLabel is "ReadyLead" or "Stage1" or "Stage2");
        }
    }

    private void LatchTripLocked(string reason, bool autoRecoverAfterFaultClear, bool forceHvAus = true)
    {
        if (!_tripLatched)
        {
            _preTripState = new G2000PreTripState
            {
                HvState = _targetHvState,
                Setpoints = _targetSetpoints.Clone(),
                UiMode = _uiMode,
                AutomaticSequenceActive = _automaticSequence is not null || _pendingHvOnAt is not null || _uiMode == G2000UiMode.Automatic,
                AutomaticStage = _automaticSequence?.CurrentStage ?? _snapshot.AutomaticStage,
                Recipe = _startupRecipe.Clone()
            };
        }

        _tripLatched = true;
        _tripReason = reason;
        _autoRecoverAfterFaultClear = autoRecoverAfterFaultClear;
        _automaticSequence = null;
        _uiMode = G2000UiMode.Manual;
        _preparedRecoveryRecipe = null;
        _preparedRecoveryHvState = null;
        _recoveryPreparationPending = false;
        if (forceHvAus)
        {
            _targetHvState = G2000HvState.HvAus;
        }

        _pendingHvOnAt = null;
        _automaticStageLabel = "Tripped";
        UpdateSnapshotLocked();
    }

    private void UpdateCommunicationHealth()
    {
        lock (_sync)
        {
            _snapshot.CommunicationHealthy = _snapshot.LastReceivedAt is not null &&
                                             _now() - _snapshot.LastReceivedAt.Value <= TimeSpan.FromSeconds(2);
            UpdateSnapshotLocked();
        }

        PublishTelemetry();
    }

    private void UpdateSnapshotLocked()
    {
        _snapshot.Connected = _initialized && !_disposed;
        _snapshot.TargetHvState = _targetHvState;
        _snapshot.UiMode = _uiMode;
        _snapshot.AutomaticStage = _automaticSequence?.CurrentStage ?? _automaticStageLabel;
        _snapshot.TripLatched = _tripLatched;
        _snapshot.TripReason = _tripReason;
        _snapshot.TargetSetpoints = _targetSetpoints.Clone();
    }

    private void PublishTelemetry()
    {
        var handler = TelemetryUpdated;
        if (handler is null)
        {
            return;
        }

        G2000TelemetrySnapshot snapshot;
        lock (_sync)
        {
            snapshot = _snapshot.Clone();
        }

        handler.Invoke(this, snapshot);
    }
}
