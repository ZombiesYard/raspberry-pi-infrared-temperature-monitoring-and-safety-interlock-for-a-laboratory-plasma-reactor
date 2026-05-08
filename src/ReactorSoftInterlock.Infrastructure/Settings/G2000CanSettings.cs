namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class G2000CanSettings
{
    public string Channel { get; set; } = "UsbBus1";

    public byte NodeId { get; set; } = 0;

    public int CommandPeriodMs { get; set; } = 100;
}
