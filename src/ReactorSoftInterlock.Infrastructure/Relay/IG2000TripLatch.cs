namespace ReactorSoftInterlock.Infrastructure.Relay;

internal interface IG2000TripLatch
{
    void LatchSoftwareTrip(string reason);
}

internal interface IG2000RecoveryPreparation
{
    Task PrepareRecoveryWhileInterlockOpenAsync(CancellationToken cancellationToken);

    Task CompletePreparedRecoveryAfterInterlockClosedAsync(CancellationToken cancellationToken);
}
