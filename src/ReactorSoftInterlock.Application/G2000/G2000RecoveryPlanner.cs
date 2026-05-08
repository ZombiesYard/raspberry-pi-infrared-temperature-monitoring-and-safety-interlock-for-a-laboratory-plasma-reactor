namespace ReactorSoftInterlock.Application.G2000;

public static class G2000RecoveryPlanner
{
    public static G2000RecoveryDecision Plan(TripRecoveryPolicy policy, G2000PreTripState? preTripState)
    {
        return policy switch
        {
            TripRecoveryPolicy.RestoreHvReady => new G2000RecoveryDecision
            {
                HvState = G2000HvState.HvReady,
                Setpoints = preTripState?.Setpoints.Clone() ?? new G2000WritableSetpoints(),
                UiMode = G2000UiMode.Manual,
                ResumeAutomaticSequence = false,
                AutomaticStage = "RecoveredToHvReady",
                Recipe = preTripState?.Recipe.Clone() ?? new G2000StartupRecipe()
            },
            TripRecoveryPolicy.RestorePreviousState when preTripState is not null => new G2000RecoveryDecision
            {
                HvState = preTripState.HvState,
                Setpoints = preTripState.Setpoints.Clone(),
                UiMode = preTripState.UiMode,
                ResumeAutomaticSequence = preTripState.AutomaticSequenceActive,
                AutomaticStage = preTripState.AutomaticStage,
                Recipe = preTripState.Recipe.Clone()
            },
            _ => new G2000RecoveryDecision
            {
                HvState = G2000HvState.HvAus,
                Setpoints = preTripState?.Setpoints.Clone() ?? new G2000WritableSetpoints(),
                UiMode = G2000UiMode.Manual,
                ResumeAutomaticSequence = false,
                AutomaticStage = "HoldHvAus",
                Recipe = preTripState?.Recipe.Clone() ?? new G2000StartupRecipe()
            }
        };
    }
}
