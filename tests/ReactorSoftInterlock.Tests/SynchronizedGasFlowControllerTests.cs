using ReactorSoftInterlock.Application;
using ReactorSoftInterlock.Application.Ports;

namespace ReactorSoftInterlock.Tests;

public sealed class SynchronizedGasFlowControllerTests
{
    [Fact]
    public async Task SerializesPeriodicReadAndSetpointWrite()
    {
        var inner = new BlockingGasFlowController();
        var controller = new SynchronizedGasFlowController(inner, new SemaphoreSlim(1, 1));

        var writeTask = controller.SetTargetFlowAsync(600, CancellationToken.None);
        await inner.FirstOperationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var readTask = controller.ReadActualFlowAsync(CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(1, inner.OperationCount);

        inner.ReleaseFirstOperation.TrySetResult();
        await Task.WhenAll(writeTask, readTask);

        Assert.Equal(2, inner.OperationCount);
        Assert.Equal(1, inner.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task ReleasesGateWhenInnerCommandFails()
    {
        var inner = new FailOnceGasFlowController();
        var controller = new SynchronizedGasFlowController(inner, new SemaphoreSlim(1, 1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.SetTargetFlowAsync(600, CancellationToken.None));

        var actual = await controller.ReadActualFlowAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(125.0, actual);
    }

    [Fact]
    public async Task SeparateControllersSharingGateCannotOverlap()
    {
        var inner = new BlockingGasFlowController();
        var gate = new SemaphoreSlim(1, 1);
        var writer = new SynchronizedGasFlowController(inner, gate);
        var reader = new SynchronizedGasFlowController(inner, gate);

        var writeTask = writer.SetTargetFlowAsync(600, CancellationToken.None);
        await inner.FirstOperationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var readTask = reader.ReadActualFlowAsync(CancellationToken.None);
        await Task.Delay(50);

        Assert.Equal(1, inner.OperationCount);
        inner.ReleaseFirstOperation.TrySetResult();
        await Task.WhenAll(writeTask, readTask);
        Assert.Equal(1, inner.MaximumConcurrentOperations);
    }

    [Fact]
    public async Task CancellationWhileWaitingDoesNotInvokeInnerController()
    {
        var inner = new BlockingGasFlowController();
        var controller = new SynchronizedGasFlowController(inner, new SemaphoreSlim(1, 1));
        var first = controller.SetTargetFlowAsync(600, CancellationToken.None);
        await inner.FirstOperationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var waiting = controller.ReadActualFlowAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, inner.OperationCount);
        inner.ReleaseFirstOperation.TrySetResult();
        await first;
    }

    private sealed class BlockingGasFlowController : IGasFlowController
    {
        private int _activeOperations;
        private int _maximumConcurrentOperations;
        private int _operationCount;

        public TaskCompletionSource FirstOperationEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstOperation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int OperationCount => Volatile.Read(ref _operationCount);

        public int MaximumConcurrentOperations => Volatile.Read(ref _maximumConcurrentOperations);

        public Task StopFlowAsync(CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);

        public Task RestoreFlowAsync(CancellationToken cancellationToken) => ExecuteAsync(cancellationToken);

        public Task SetTargetFlowAsync(double targetFlowMlMin, CancellationToken cancellationToken) =>
            ExecuteAsync(cancellationToken);

        public async Task<double?> ReadActualFlowAsync(CancellationToken cancellationToken)
        {
            await ExecuteAsync(cancellationToken);
            return 125.0;
        }

        private async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _activeOperations);
            UpdateMaximum(active);
            var operationNumber = Interlocked.Increment(ref _operationCount);
            try
            {
                if (operationNumber == 1)
                {
                    FirstOperationEntered.TrySetResult();
                    await ReleaseFirstOperation.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeOperations);
            }
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrentOperations);
                if (active <= current ||
                    Interlocked.CompareExchange(ref _maximumConcurrentOperations, active, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class FailOnceGasFlowController : IGasFlowController
    {
        private bool _shouldFail = true;

        public Task StopFlowAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RestoreFlowAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetTargetFlowAsync(double targetFlowMlMin, CancellationToken cancellationToken)
        {
            if (_shouldFail)
            {
                _shouldFail = false;
                throw new InvalidOperationException("Expected test failure.");
            }

            return Task.CompletedTask;
        }

        public Task<double?> ReadActualFlowAsync(CancellationToken cancellationToken) =>
            Task.FromResult<double?>(125.0);
    }
}
