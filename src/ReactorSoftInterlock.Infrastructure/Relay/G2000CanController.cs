using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Relay;

public sealed class G2000CanController : IRelayBankController, IDisposable
{
    private readonly G2000CanSettings _settings;
    private readonly ushort _channelHandle;
    private readonly TimeSpan _commandPeriod;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private CancellationTokenSource? _senderCts;
    private Task? _senderTask;
    private byte[] _currentCommand = G2000CanProtocol.CreateCanBusStopData();
    private bool _initialized;
    private bool _disposed;

    public G2000CanController(G2000CanSettings settings)
    {
        _settings = settings;
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
    }

    public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
    {
        return SetCommandAsync(G2000CanProtocol.CreateCanBusStopData(), RelayAction.StopSent, cancellationToken);
    }

    public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
    {
        return SetCommandAsync(G2000CanProtocol.CreateCanBusHvReadyData(), RelayAction.ResetSent, cancellationToken);
    }

    public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
    {
        return SetCommandAsync(G2000CanProtocol.CreateCanBusStopData(), RelayAction.TestStopSent, cancellationToken);
    }

    public Task OpenAllInterlocksAsync(CancellationToken cancellationToken)
    {
        return SetCommandAsync(G2000CanProtocol.CreateCanBusStopData(), RelayAction.StopSent, cancellationToken);
    }

    public Task CloseAllInterlocksAsync(CancellationToken cancellationToken)
    {
        return SetCommandAsync(G2000CanProtocol.CreateCanBusHvReadyData(), RelayAction.ResetSent, cancellationToken);
    }

    public Task SetChannelClosedAsync(int channelNumber, bool closed, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("G2000 CAN mode does not support per-channel relay bank control.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _senderCts?.Cancel();
        try
        {
            _senderTask?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static inner => inner is TaskCanceledException or OperationCanceledException))
        {
            // Expected when shutting down the keepalive loop.
        }
        finally
        {
            _senderCts?.Dispose();
            _writeGate.Dispose();
        }

        if (_initialized)
        {
            PcanBasicNative.Uninitialize(_channelHandle);
        }
    }

    private async Task<RelayAction> SetCommandAsync(byte[] command, RelayAction action, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureInitialized();
        EnsureSenderLoop();

        lock (_sync)
        {
            _currentCommand = command;
        }

        await WriteCurrentCommandAsync(cancellationToken).ConfigureAwait(false);
        return action;
    }

    private void EnsureSenderLoop()
    {
        lock (_sync)
        {
            if (_senderTask is not null)
            {
                return;
            }

            _senderCts = new CancellationTokenSource();
            _senderTask = Task.Run(() => RunSenderLoopAsync(_senderCts.Token));
        }
    }

    private async Task RunSenderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_commandPeriod);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await WriteCurrentCommandAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task WriteCurrentCommandAsync(CancellationToken cancellationToken)
    {
        byte[] command;
        lock (_sync)
        {
            command = (byte[])_currentCommand.Clone();
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var message = new PcanBasicNative.TPCANMsg
            {
                ID = G2000CanProtocol.GetCommandId(_settings.NodeId),
                MSGTYPE = PcanBasicNative.PcanMessageStandard,
                LEN = 8,
                DATA = command
            };

            var status = PcanBasicNative.Write(_channelHandle, ref message);
            if (status != PcanBasicNative.PcanErrorOk)
            {
                throw new InvalidOperationException($"PCAN write failed with status 0x{status:X} on channel {_settings.Channel}.");
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        var status = PcanBasicNative.Initialize(_channelHandle, PcanBasicNative.PcanBaud125K, 0, 0, 0);
        if (status != PcanBasicNative.PcanErrorOk)
        {
            throw new InvalidOperationException($"PCAN initialization failed with status 0x{status:X} on channel {_settings.Channel}.");
        }

        _initialized = true;
    }
}
