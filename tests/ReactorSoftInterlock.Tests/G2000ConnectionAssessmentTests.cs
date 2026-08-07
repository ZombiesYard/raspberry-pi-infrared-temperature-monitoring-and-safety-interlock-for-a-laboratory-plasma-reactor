using ReactorSoftInterlock.Application.G2000;

namespace ReactorSoftInterlock.Tests;

public sealed class G2000ConnectionAssessmentTests
{
    [Fact]
    public void ConfirmsOnlyConnectedHealthyRecentTelemetry()
    {
        var now = DateTimeOffset.Parse("2026-08-07T12:00:00Z");
        var snapshot = new G2000TelemetrySnapshot
        {
            Connected = true,
            CommunicationHealthy = true,
            LastReceivedAt = now - TimeSpan.FromSeconds(1)
        };

        var result = G2000ConnectionAssessment.Assess(snapshot, "stale PCAN error", now);

        Assert.Equal(G2000ConnectionAssessmentKind.Confirmed, result);
    }

    [Fact]
    public void RejectsHealthyFlagWhenLastTelemetryIsStale()
    {
        var now = DateTimeOffset.Parse("2026-08-07T12:00:00Z");
        var snapshot = new G2000TelemetrySnapshot
        {
            Connected = true,
            CommunicationHealthy = true,
            LastReceivedAt = now - TimeSpan.FromSeconds(3)
        };

        var result = G2000ConnectionAssessment.Assess(snapshot, null, now);

        Assert.Equal(G2000ConnectionAssessmentKind.NoRecentTelemetry, result);
    }

    [Fact]
    public void ReportsMissingTelemetryWhenPcanIsOpenButG2000DoesNotReply()
    {
        var snapshot = new G2000TelemetrySnapshot
        {
            Connected = true,
            CommunicationHealthy = false,
            LastSentAt = DateTimeOffset.UtcNow
        };

        var result = G2000ConnectionAssessment.Assess(snapshot, null);

        Assert.Equal(G2000ConnectionAssessmentKind.NoRecentTelemetry, result);
    }

    [Fact]
    public void ReportsPcanFailureWhenNoCommunicationAndInitializationFailed()
    {
        var result = G2000ConnectionAssessment.Assess(
            new G2000TelemetrySnapshot(),
            "PCAN_ERROR_INITIALIZE");

        Assert.Equal(G2000ConnectionAssessmentKind.PcanUnavailable, result);
    }
}
