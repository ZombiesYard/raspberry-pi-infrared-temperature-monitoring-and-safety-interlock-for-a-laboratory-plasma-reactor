using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;

namespace ReactorSoftInterlock.Infrastructure.Logging;

/// <summary>
/// Observes failed automatic interlock output calls without changing their
/// result, exception, ordering, or cancellation behavior.
/// </summary>
public sealed class ExperimentRelayFailureObserver : IRelayController
{
    private readonly IRelayController _inner;
    private readonly Action<string, string, string> _recordOutcome;

    public ExperimentRelayFailureObserver(
        IRelayController inner,
        Action<string, string, string> recordOutcome)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _recordOutcome = recordOutcome ?? throw new ArgumentNullException(nameof(recordOutcome));
    }

    public Task<RelayAction> StopAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync("automatic-stop", _inner.StopAsync, cancellationToken);
    }

    public Task<RelayAction> ResetAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync("automatic-reset", _inner.ResetAsync, cancellationToken);
    }

    public Task<RelayAction> TestStopAsync(CancellationToken cancellationToken)
    {
        return ExecuteAsync("automatic-test-stop", _inner.TestStopAsync, cancellationToken);
    }

    private async Task<RelayAction> ExecuteAsync(
        string action,
        Func<CancellationToken, Task<RelayAction>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            RecordSafely(action, "cancelled", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            RecordSafely(action, "failed", ex.Message);
            throw;
        }
    }

    private void RecordSafely(string action, string outcome, string details)
    {
        try
        {
            _recordOutcome(action, outcome, details);
        }
        catch
        {
            // Evidence recording must never replace or mask the control exception.
        }
    }
}
