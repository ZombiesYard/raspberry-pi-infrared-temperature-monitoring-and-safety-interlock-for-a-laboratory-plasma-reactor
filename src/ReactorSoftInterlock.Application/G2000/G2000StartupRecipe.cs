namespace ReactorSoftInterlock.Application.G2000;

public sealed class G2000StartupRecipe
{
    public double Stage1VoltageV { get; set; } = 63.0;

    public int Stage1DurationMs { get; set; } = 1000;

    public double Stage2VoltageV { get; set; } = 43.0;

    public bool Stage2HoldEnabled { get; set; } = true;

    public bool EnterHvReadyBeforeRun { get; set; } = true;

    public bool EnterHvOnAtStart { get; set; } = true;

    public G2000StartupRecipe Clone()
    {
        return new G2000StartupRecipe
        {
            Stage1VoltageV = Stage1VoltageV,
            Stage1DurationMs = Stage1DurationMs,
            Stage2VoltageV = Stage2VoltageV,
            Stage2HoldEnabled = Stage2HoldEnabled,
            EnterHvReadyBeforeRun = EnterHvReadyBeforeRun,
            EnterHvOnAtStart = EnterHvOnAtStart
        };
    }
}
