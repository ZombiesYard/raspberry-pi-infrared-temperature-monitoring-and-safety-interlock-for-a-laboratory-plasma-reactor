using System.Buffers.Binary;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Gas;

public sealed class Amc2100GasFlowController : IGasFlowController
{
    private readonly Amc2100Settings _settings;
    private readonly IAmc2100SerialPortFactory _portFactory;
    private readonly object _sync = new();
    private double? _cachedRestoreSetpointMlMin;

    public Amc2100GasFlowController(Amc2100Settings settings)
        : this(settings, new Amc2100SerialPortFactory())
    {
    }

    internal Amc2100GasFlowController(
        Amc2100Settings settings,
        IAmc2100SerialPortFactory portFactory)
    {
        _settings = settings.Clone();
        _settings.Normalize();
        _portFactory = portFactory;
    }

    public Task StopFlowAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var port = OpenPort();
            if (_settings.ForceDigitalControlMode)
            {
                EnsureDigitalMode(port, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var currentSetpoint = ReadFloatRegisterPair(port, (ushort)_settings.SetpointHighRegister);
            if (currentSetpoint > 0)
            {
                _cachedRestoreSetpointMlMin = currentSetpoint;
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteAndVerifySetpoint(port, 0f);
            return Task.CompletedTask;
        }
    }

    public Task RestoreFlowAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var restoreTarget = _cachedRestoreSetpointMlMin ?? _settings.FallbackRestoreSetpointMlMin;
            if (restoreTarget <= 0)
            {
                return Task.CompletedTask;
            }

            using var port = OpenPort();
            if (_settings.ForceDigitalControlMode)
            {
                EnsureDigitalMode(port, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteAndVerifySetpoint(port, (float)restoreTarget);
            return Task.CompletedTask;
        }
    }

    public Task SetTargetFlowAsync(double targetFlowMlMin, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedTarget = Math.Max(0d, targetFlowMlMin);
            using var port = OpenPort();
            if (_settings.ForceDigitalControlMode)
            {
                EnsureDigitalMode(port, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteAndVerifySetpoint(port, (float)normalizedTarget);
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
            cancellationToken.ThrowIfCancellationRequested();
            using var port = OpenPort();
            var actualFlow = ReadFloatRegisterPair(port, (ushort)_settings.ActualFlowHighRegister);
            return Task.FromResult<double?>((double)actualFlow);
        }
    }

    private IAmc2100SerialPort OpenPort() => _portFactory.Open(_settings);

    private void EnsureDigitalMode(IAmc2100SerialPort port, CancellationToken cancellationToken)
    {
        var request = Amc2100ModbusFrameBuilder.BuildWriteSingleRegister(
            (byte)_settings.SlaveAddress,
            (ushort)_settings.ControlModeRegister,
            (ushort)_settings.DigitalControlModeValue);

        cancellationToken.ThrowIfCancellationRequested();
        WriteAndExpectEcho(port, request);
    }

    private float ReadFloatRegisterPair(IAmc2100SerialPort port, ushort startAddress)
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

    private void WriteFloatRegisterPair(IAmc2100SerialPort port, ushort startAddress, float value)
    {
        var registers = Amc2100ModbusFrameBuilder.FloatToRegisters(value);
        var request = Amc2100ModbusFrameBuilder.BuildWriteMultipleRegisters((byte)_settings.SlaveAddress, startAddress, registers);
        port.Write(request, 0, request.Length);
        var response = ReadExact(port, 8);
        ValidateResponse(response, 0x10);
        if (!Amc2100ModbusFrameBuilder.ValidateWriteMultipleRegistersResponse(request, response))
        {
            throw new InvalidOperationException("AMC2100 write response did not confirm the requested setpoint registers.");
        }
    }

    private void WriteAndExpectEcho(IAmc2100SerialPort port, byte[] request)
    {
        port.Write(request, 0, request.Length);
        var response = ReadExact(port, 8);
        ValidateResponse(response, request[1]);
        if (!Amc2100ModbusFrameBuilder.ValidateWriteSingleRegisterResponse(request, response))
        {
            throw new InvalidOperationException("AMC2100 write response did not echo the requested control register.");
        }
    }

    private void WriteAndVerifySetpoint(IAmc2100SerialPort port, float target)
    {
        WriteFloatRegisterPair(port, (ushort)_settings.SetpointHighRegister, target);
        var readBack = ReadFloatRegisterPair(port, (ushort)_settings.SetpointHighRegister);
        var tolerance = Math.Max(0.1f, Math.Abs(target) * 0.0001f);
        if (!float.IsFinite(readBack) || Math.Abs(readBack - target) > tolerance)
        {
            throw new InvalidOperationException(
                $"AMC2100 setpoint verification failed: requested {target:0.###} mL/min, read back {readBack:0.###} mL/min.");
        }
    }

    private static byte[] ReadExact(IAmc2100SerialPort port, int length)
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
