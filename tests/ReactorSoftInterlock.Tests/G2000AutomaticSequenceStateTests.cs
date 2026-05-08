using ReactorSoftInterlock.Application.G2000;

namespace ReactorSoftInterlock.Tests;

public sealed class G2000AutomaticSequenceStateTests
{
    [Fact]
    public void Sequence_SwitchesFromStage1ToStage2AfterConfiguredDuration()
    {
        var recipe = new G2000StartupRecipe
        {
            Stage1VoltageV = 63.0,
            Stage1DurationMs = 1000,
            Stage2VoltageV = 43.0,
            Stage2HoldEnabled = true
        };

        var startedAt = new DateTimeOffset(2026, 5, 8, 12, 0, 0, TimeSpan.Zero);
        var sequence = new G2000AutomaticSequenceState(recipe, startedAt);

        Assert.Equal("Stage1", sequence.CurrentStage);
        Assert.False(sequence.TryAdvance(startedAt.AddMilliseconds(999), out _));
        Assert.True(sequence.TryAdvance(startedAt.AddMilliseconds(1000), out var stage2Voltage));
        Assert.Equal("Stage2", sequence.CurrentStage);
        Assert.Equal(43.0, stage2Voltage);
    }

    [Fact]
    public void Sequence_CanMarkCompletionWhenStage2ShouldNotHold()
    {
        var recipe = new G2000StartupRecipe
        {
            Stage1VoltageV = 63.0,
            Stage1DurationMs = 1000,
            Stage2VoltageV = 43.0,
            Stage2HoldEnabled = false
        };

        var sequence = new G2000AutomaticSequenceState(recipe, DateTimeOffset.UtcNow);
        Assert.True(sequence.TryAdvance(DateTimeOffset.UtcNow.AddSeconds(2), out _));
        Assert.True(sequence.IsComplete);
    }
}
