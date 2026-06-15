using ReactorSoftInterlock.Application;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Tests;

public sealed class SynchronizedRelayBankControllerTests
{
    [Fact]
    public async Task CommandsWaitForPreviousCommandToReleaseGate()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var inner = new BlockingRelayBank(events, firstEntered, releaseFirst, secondEntered);
        using var gate = new SemaphoreSlim(1, 1);
        var controller = new SynchronizedRelayBankController(inner, gate);

        var first = controller.OpenAllInterlocksAsync(CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var second = controller.CloseAllInterlocksAsync(CancellationToken.None);
        await Task.Yield();

        var early = await Task.WhenAny(secondEntered.Task, Task.Delay(100));
        Assert.NotSame(secondEntered.Task, early);

        releaseFirst.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(["open-start", "open-end", "close"], events);
    }

    private sealed class BlockingRelayBank(
        List<string> events,
        TaskCompletionSource firstEntered,
        TaskCompletionSource releaseFirst,
        TaskCompletionSource secondEntered) : IRelayBankController
    {
        public Task<RelayAction> StopAsync(CancellationToken cancellationToken) => Task.FromResult(RelayAction.StopSent);
        public Task<RelayAction> ResetAsync(CancellationToken cancellationToken) => Task.FromResult(RelayAction.ResetSent);
        public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken) => Task.FromResult(RelayAction.TestStopSent);

        public async Task OpenAllInterlocksAsync(CancellationToken cancellationToken)
        {
            events.Add("open-start");
            firstEntered.SetResult();
            await releaseFirst.Task.WaitAsync(cancellationToken);
            events.Add("open-end");
        }

        public Task CloseAllInterlocksAsync(CancellationToken cancellationToken)
        {
            events.Add("close");
            secondEntered.SetResult();
            return Task.CompletedTask;
        }

        public Task SetChannelClosedAsync(int channelNumber, bool closed, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
