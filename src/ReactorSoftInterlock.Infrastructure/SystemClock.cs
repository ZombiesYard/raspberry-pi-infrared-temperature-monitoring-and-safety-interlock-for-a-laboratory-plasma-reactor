using ReactorSoftInterlock.Application.Ports;

namespace ReactorSoftInterlock.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
