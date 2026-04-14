using System.IO.Ports;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Relay;

public sealed class SerialRelayController : IRelayController
{
    private readonly RelaySettings _settings;

    public SerialRelayController(RelaySettings settings)
    {
        _settings = settings;
    }

    public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
    {
        Send(HexCommandParser.Parse(_settings.StopCommandHex));
        return Task.FromResult(RelayAction.StopSent);
    }

    public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
    {
        Send(HexCommandParser.Parse(_settings.ResetCommandHex));
        return Task.FromResult(RelayAction.ResetSent);
    }

    public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
    {
        Send(HexCommandParser.Parse(_settings.StopCommandHex));
        return Task.FromResult(RelayAction.TestStopSent);
    }

    private void Send(byte[] command)
    {
        if (command.Length == 0)
        {
            throw new InvalidOperationException("Relay command is empty. Configure StopCommandHex and ResetCommandHex before using SerialRelay.");
        }

        using var port = new SerialPort(_settings.PortName, _settings.BaudRate)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000
        };
        port.Open();
        port.Write(command, 0, command.Length);
    }
}
