namespace ReactorSoftInterlock.Domain;

public sealed record InterlockDecision(
    MonitorStatus Status,
    RelayAction RelayAction,
    string AlarmReason,
    bool ShouldSendStop);
