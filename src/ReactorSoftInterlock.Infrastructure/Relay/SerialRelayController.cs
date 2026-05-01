using System.IO.Ports;
using System.Text;
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
        Send(_settings.StopCommandHex);
        return Task.FromResult(RelayAction.StopSent);
    }

    public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
    {
        Send(_settings.ResetCommandHex);
        return Task.FromResult(RelayAction.ResetSent);
    }

    public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
    {
        Send(_settings.StopCommandHex);
        return Task.FromResult(RelayAction.TestStopSent);
    }

    private void Send(string commandText)
    {
        var commands = RelayCommandTextParser.Parse(commandText);
        if (commands.Count == 0)
        {
            throw new InvalidOperationException("Relay command is empty. Configure Stop command and Reset command before using the serial relay.");
        }

        using var port = new SerialPort(_settings.PortName, _settings.BaudRate)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            Handshake = Handshake.None,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Encoding = Encoding.ASCII,
            NewLine = "\r\n"
        };
        port.Open();

        foreach (var command in commands)
        {
            port.DiscardInBuffer();
            port.Write(command + port.NewLine);
            Thread.Sleep(250);

            var response = port.ReadExisting();
            if (!string.IsNullOrWhiteSpace(response) &&
                !response.Contains("OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Relay command '{command}' failed: {response.Trim()}");
            }
        }
    }
}
