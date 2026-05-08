namespace ReactorSoftInterlock.Application.G2000;

public sealed class G2000RecoveryDecision
{
    public G2000HvState HvState { get; set; } = G2000HvState.HvAus;

    public G2000WritableSetpoints Setpoints { get; set; } = new();

    public G2000UiMode UiMode { get; set; } = G2000UiMode.Manual;

    public bool ResumeAutomaticSequence { get; set; }

    public string AutomaticStage { get; set; } = "Idle";

    public G2000StartupRecipe Recipe { get; set; } = new();
}
