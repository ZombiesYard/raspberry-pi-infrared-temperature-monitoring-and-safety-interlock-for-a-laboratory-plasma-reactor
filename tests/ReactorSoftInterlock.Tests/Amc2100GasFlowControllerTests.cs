using System.Buffers.Binary;
using ReactorSoftInterlock.Infrastructure.Gas;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class Amc2100GasFlowControllerTests
{
    [Fact]
    public async Task SetTargetFlowWritesThenReadsBackSetpoint()
    {
        var factory = new FakePortFactory { Readback = 600f };
        var controller = CreateController(factory);

        await controller.SetTargetFlowAsync(600, CancellationToken.None);

        Assert.Equal([0x10, 0x03], factory.Port.Writes.Select(static frame => frame[1]));
        Assert.True(factory.Port.Disposed);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SetTargetFlowRejectsWrongWriteConfirmation(bool wrongAddress, bool wrongCount)
    {
        var factory = new FakePortFactory
        {
            Readback = 600f,
            WrongWriteAddress = wrongAddress,
            WrongRegisterCount = wrongCount
        };
        var controller = CreateController(factory);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SetTargetFlowAsync(600, CancellationToken.None));

        Assert.Contains("confirm", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(factory.Port.Writes);
    }

    [Fact]
    public async Task SetTargetFlowRejectsMismatchedReadback()
    {
        var factory = new FakePortFactory { Readback = 500f };
        var controller = CreateController(factory);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SetTargetFlowAsync(600, CancellationToken.None));

        Assert.Contains("verification failed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([0x10, 0x03], factory.Port.Writes.Select(static frame => frame[1]));
    }

    [Fact]
    public async Task SetTargetFlowPropagatesMissingResponseAsTimeout()
    {
        var factory = new FakePortFactory { SuppressResponses = true };
        var controller = CreateController(factory);

        await Assert.ThrowsAsync<TimeoutException>(
            () => controller.SetTargetFlowAsync(600, CancellationToken.None));
    }

    [Fact]
    public async Task SetTargetFlowInDigitalModeWritesControlThenSetpointAndReadsBack()
    {
        var factory = new FakePortFactory { Readback = 600f };
        var controller = CreateController(factory, forceDigitalControlMode: true);

        await controller.SetTargetFlowAsync(600, CancellationToken.None);

        Assert.Equal([0x06, 0x10, 0x03], factory.Port.Writes.Select(static frame => frame[1]));
    }

    [Fact]
    public async Task SetTargetFlowInDigitalModeRejectsWrongControlRegisterEcho()
    {
        var factory = new FakePortFactory { WrongControlRegisterEcho = true };
        var controller = CreateController(factory, forceDigitalControlMode: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SetTargetFlowAsync(600, CancellationToken.None));

        Assert.Contains("echo", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(factory.Port.Writes);
    }

    private static Amc2100GasFlowController CreateController(
        FakePortFactory factory,
        bool forceDigitalControlMode = false)
    {
        return new Amc2100GasFlowController(
            new Amc2100Settings
            {
                Enabled = true,
                PortName = "TEST",
                BaudRate = 19200,
                SlaveAddress = 1,
                ForceDigitalControlMode = forceDigitalControlMode
            },
            factory);
    }

    private sealed class FakePortFactory : IAmc2100SerialPortFactory
    {
        public FakePort Port { get; } = new();

        public float Readback
        {
            get => Port.Readback;
            init => Port.Readback = value;
        }

        public bool WrongWriteAddress
        {
            init => Port.WrongWriteAddress = value;
        }

        public bool WrongRegisterCount
        {
            init => Port.WrongRegisterCount = value;
        }

        public bool SuppressResponses
        {
            init => Port.SuppressResponses = value;
        }

        public bool WrongControlRegisterEcho
        {
            init => Port.WrongControlRegisterEcho = value;
        }

        public IAmc2100SerialPort Open(Amc2100Settings settings) => Port;
    }

    private sealed class FakePort : IAmc2100SerialPort
    {
        private readonly Queue<byte> _responseBytes = new();

        public List<byte[]> Writes { get; } = [];

        public float Readback { get; set; }

        public bool WrongWriteAddress { get; set; }

        public bool WrongRegisterCount { get; set; }

        public bool SuppressResponses { get; set; }

        public bool WrongControlRegisterEcho { get; set; }

        public bool Disposed { get; private set; }

        public void Write(byte[] buffer, int offset, int count)
        {
            var request = buffer.AsSpan(offset, count).ToArray();
            Writes.Add(request);
            if (SuppressResponses)
            {
                return;
            }

            var response = request[1] switch
            {
                0x06 => BuildSingleWriteResponse(request),
                0x10 => BuildWriteResponse(request),
                0x03 => BuildReadResponse(request[0], Readback),
                _ => throw new InvalidOperationException("Unexpected fake request.")
            };
            foreach (var value in response)
            {
                _responseBytes.Enqueue(value);
            }
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            var copied = 0;
            while (copied < count && _responseBytes.TryDequeue(out var value))
            {
                buffer[offset + copied] = value;
                copied++;
            }

            return copied;
        }

        public void Dispose() => Disposed = true;

        private byte[] BuildSingleWriteResponse(byte[] request)
        {
            var response = request.ToArray();
            if (!WrongControlRegisterEcho)
            {
                return response;
            }

            response[3]++;
            var crc = Amc2100ModbusFrameBuilder.ComputeCrc(response.AsSpan(0, 6));
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(6, 2), crc);
            return response;
        }

        private byte[] BuildWriteResponse(byte[] request)
        {
            var data = request.AsSpan(0, 6).ToArray();
            if (WrongWriteAddress)
            {
                data[3]++;
            }
            if (WrongRegisterCount)
            {
                data[5] = 1;
            }
            return AddCrc(data);
        }

        private static byte[] BuildReadResponse(byte slaveAddress, float value)
        {
            var registers = Amc2100ModbusFrameBuilder.FloatToRegisters(value);
            var data = new byte[7];
            data[0] = slaveAddress;
            data[1] = 0x03;
            data[2] = 0x04;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(3, 2), registers[0]);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(5, 2), registers[1]);
            return AddCrc(data);
        }

        private static byte[] AddCrc(ReadOnlySpan<byte> data)
        {
            var response = new byte[data.Length + 2];
            data.CopyTo(response);
            var crc = Amc2100ModbusFrameBuilder.ComputeCrc(data);
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(data.Length, 2), crc);
            return response;
        }
    }
}
