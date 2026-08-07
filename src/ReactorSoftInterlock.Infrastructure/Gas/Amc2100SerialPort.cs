using System.IO.Ports;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Gas;

internal interface IAmc2100SerialPort : IDisposable
{
    void Write(byte[] buffer, int offset, int count);

    int Read(byte[] buffer, int offset, int count);
}

internal interface IAmc2100SerialPortFactory
{
    IAmc2100SerialPort Open(Amc2100Settings settings);
}

internal sealed class Amc2100SerialPortFactory : IAmc2100SerialPortFactory
{
    public IAmc2100SerialPort Open(Amc2100Settings settings)
    {
        var port = new SerialPort(settings.PortName, settings.BaudRate)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            Handshake = Handshake.None,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One
        };

        try
        {
            port.Open();
            port.DiscardInBuffer();
            port.DiscardOutBuffer();
            return new Amc2100SerialPort(port);
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }
}

internal sealed class Amc2100SerialPort : IAmc2100SerialPort
{
    private readonly SerialPort _inner;

    public Amc2100SerialPort(SerialPort inner)
    {
        _inner = inner;
    }

    public void Write(byte[] buffer, int offset, int count) =>
        _inner.Write(buffer, offset, count);

    public int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    public void Dispose() => _inner.Dispose();
}
