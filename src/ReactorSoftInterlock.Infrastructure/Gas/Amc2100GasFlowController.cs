using System.Buffers.Binary;
using System.IO.Ports;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Gas;

public sealed class Amc2100GasFlowController : IGasFlowController
{
    private readonly Amc2100Settings _settings;
    private readonly object _sync = new();
    private double? _cachedRestoreSetpointMlMin;

    public Amc2100GasFlowController(Amc2100Settings settings)
    {
        _settings = settings;
        _settings.Normalize();
    }

    public Task StopFlowAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            using var port = OpenPort();
            if (_settings.ForceDigitalControlMode)
            {
                EnsureDigitalMode(port);
            }

            var currentSetpoint = ReadFloatRegisterPair(port, (ushort)_settings.SetpointHighRegister);
            if (currentSetpoint > 0)
            {
                _cachedRestoreSetpointMlMin = currentSetpoint;
            }

            WriteFloatRegisterPair(port, (ushort)_settings.SetpointHighRegister, 0f);
            return Task.CompletedTask;
        }
    }

    public Task RestoreFlowAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            var restoreTarget = _cachedRestoreSetpointMlMin ?? _settings.FallbackRestoreSetpointMlMin;
            if (restoreTarget <= 0)
            {
                return Task.CompletedTask;
            }

            using var port = OpenPort();
            if (_settings.ForceDigitalControlMode)
            {
                EnsureDigitalMode(port);
            }

            WriteFloatRegisterPair(port, (ushort)_settings.SetpointHighRegister, (float)restoreTarget);
            return Task.CompletedTask;
        }
    }

    public Task SetTargetFlowAsync(double targetFlowMlMin, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            var normalizedTarget = Math.Max(0d, targetFlowMlMin);
            using var port = OpenPort();
            if (_settings.ForceDigitalControlMode)
            {
                EnsureDigitalMode(port);
            }

            WriteFloatRegisterPair(port, (ushort)_settings.SetpointHighRegister, (float)normalizedTarget);
            if (normalizedTarget > 0)
            {
                _cachedRestoreSetpointMlMin = normalizedTarget;
            }

            return Task.CompletedTask;
        }
    }

    public Task<double?> ReadActualFlowAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            using var port = OpenPort();
            var actualFlow = ReadFloatRegisterPair(port, (ushort)_settings.ActualFlowHighRegister);
            return Task.FromResult<double?>((double)actualFlow);
        }
    }

    private SerialPort OpenPort()
    {
        var port = new SerialPort(_settings.PortName, _settings.BaudRate)
        {
            ReadTimeout = 1000,
            WriteTimeout = 1000,
            Handshake = Handshake.None,
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One
        };
        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        return port;
    }

    private void EnsureDigitalMode(SerialPort port)
    {
        var request = Amc2100ModbusFrameBuilder.BuildWriteSingleRegister(
            (byte)_settings.SlaveAddress,
            (ushort)_settings.ControlModeRegister,
            (ushort)_settings.DigitalControlModeValue);

        WriteAndExpectEcho(port, request);
    }

    private float ReadFloatRegisterPair(SerialPort port, ushort startAddress)
    {
        var request = Amc2100ModbusFrameBuilder.BuildReadHoldingRegisters((byte)_settings.SlaveAddress, startAddress, 2);
        port.Write(request, 0, request.Length);
        var response = ReadExact(port, 9);
        ValidateResponse(response, 0x03);

        if (response[2] != 0x04)
        {
            throw new InvalidOperationException($"AMC2100 returned unexpected byte count {response[2]} while reading register {startAddress}.");
        }

        var high = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(3, 2));
        var low = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(5, 2));
        return Amc2100ModbusFrameBuilder.RegistersToFloat(high, low);
    }

    private void WriteFloatRegisterPair(SerialPort port, ushort startAddress, float value)
    {
        var registers = Amc2100ModbusFrameBuilder.FloatToRegisters(value);
        var request = Amc2100ModbusFrameBuilder.BuildWriteMultipleRegisters((byte)_settings.SlaveAddress, startAddress, registers);
        port.Write(request, 0, request.Length);
        var response = ReadExact(port, 8);
        ValidateResponse(response, 0x10);
    }

    private void WriteAndExpectEcho(SerialPort port, byte[] request)
    {
        port.Write(request, 0, request.Length);
        var response = ReadExact(port, 8);
        ValidateResponse(response, request[1]);
    }

    private static byte[] ReadExact(SerialPort port, int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = port.Read(buffer, offset, length - offset);
            if (read <= 0)
            {
                throw new TimeoutException($"AMC2100 serial read timed out after receiving {offset} of {length} bytes.");
            }

            offset += read;
        }

        return buffer;
    }

    private void ValidateResponse(byte[] response, byte expectedFunctionCode)
    {
        if (!Amc2100ModbusFrameBuilder.ValidateCrc(response))
        {
            throw new InvalidOperationException("AMC2100 response CRC check failed.");
        }

        if (response[0] != (byte)_settings.SlaveAddress)
        {
            throw new InvalidOperationException($"AMC2100 replied from slave {response[0]}, expected {_settings.SlaveAddress}.");
        }

        if (response[1] == (expectedFunctionCode | 0x80))
        {
            var exceptionCode = response[2];
            throw new InvalidOperationException($"AMC2100 Modbus exception {exceptionCode} for function 0x{expectedFunctionCode:X2}.");
        }

        if (response[1] != expectedFunctionCode)
        {
            throw new InvalidOperationException(
                $"AMC2100 returned function 0x{response[1]:X2}, expected 0x{expectedFunctionCode:X2}.");
        }
    }
}
