namespace ReactorSoftInterlock.Domain;

public enum MonitorStatus
{
    Idle,
    Monitoring,
    NoReading,
    Tripped,
    RelayTestFailed
}
