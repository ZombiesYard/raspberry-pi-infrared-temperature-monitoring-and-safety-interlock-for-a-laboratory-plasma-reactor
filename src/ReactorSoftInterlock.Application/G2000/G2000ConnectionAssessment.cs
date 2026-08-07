namespace ReactorSoftInterlock.Application.G2000;

public enum G2000ConnectionAssessmentKind
{
    Confirmed,
    PcanUnavailable,
    NoRecentTelemetry
}

public static class G2000ConnectionAssessment
{
    public static G2000ConnectionAssessmentKind Assess(
        G2000TelemetrySnapshot snapshot,
        string? pcanConnectionError,
        DateTimeOffset? now = null,
        TimeSpan? maximumTelemetryAge = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var evaluatedAt = now ?? DateTimeOffset.UtcNow;
        var maximumAge = maximumTelemetryAge ?? TimeSpan.FromSeconds(2);
        var telemetryAge = snapshot.LastReceivedAt is null
            ? (TimeSpan?)null
            : evaluatedAt - snapshot.LastReceivedAt.Value;

        if (snapshot.Connected &&
            snapshot.CommunicationHealthy &&
            telemetryAge is not null &&
            telemetryAge.Value >= TimeSpan.Zero &&
            telemetryAge.Value <= maximumAge)
        {
            return G2000ConnectionAssessmentKind.Confirmed;
        }

        return string.IsNullOrWhiteSpace(pcanConnectionError)
            ? G2000ConnectionAssessmentKind.NoRecentTelemetry
            : G2000ConnectionAssessmentKind.PcanUnavailable;
    }
}
