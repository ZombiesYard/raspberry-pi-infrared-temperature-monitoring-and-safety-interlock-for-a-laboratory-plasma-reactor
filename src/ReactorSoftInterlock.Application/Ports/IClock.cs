namespace ReactorSoftInterlock.Application.Ports;

public interface IClock
{
    DateTimeOffset Now { get; }
}
