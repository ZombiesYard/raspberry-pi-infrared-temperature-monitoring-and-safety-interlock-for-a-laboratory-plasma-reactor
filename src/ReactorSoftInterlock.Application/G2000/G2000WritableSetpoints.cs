namespace ReactorSoftInterlock.Application.G2000;

public sealed class G2000WritableSetpoints
{
    public double VoltageV { get; set; } = 43.0;

    public double FrequencyKhz { get; set; } = 110.0;

    public double DutyPercent { get; set; } = 45.0;

    public double TonMs { get; set; } = 1.0;

    public double ToffMs { get; set; } = 0.0;

    public G2000WritableSetpoints Clone()
    {
        return new G2000WritableSetpoints
        {
            VoltageV = VoltageV,
            FrequencyKhz = FrequencyKhz,
            DutyPercent = DutyPercent,
            TonMs = TonMs,
            ToffMs = ToffMs
        };
    }
}
