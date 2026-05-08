using System;

namespace ReactorSoftInterlock.Application.G2000;

public sealed class G2000AutomaticSequenceState
{
    private readonly DateTimeOffset _startedAt;
    private bool _stage2Applied;

    public G2000AutomaticSequenceState(G2000StartupRecipe recipe, DateTimeOffset startedAt)
    {
        Recipe = recipe.Clone();
        _startedAt = startedAt;
    }

    public G2000StartupRecipe Recipe { get; }

    public string CurrentStage => _stage2Applied ? "Stage2" : "Stage1";

    public bool IsComplete { get; private set; }

    public double CurrentVoltageV => _stage2Applied ? Recipe.Stage2VoltageV : Recipe.Stage1VoltageV;

    public bool TryAdvance(DateTimeOffset now, out double nextVoltageV)
    {
        nextVoltageV = CurrentVoltageV;
        if (IsComplete || _stage2Applied)
        {
            return false;
        }

        if (now - _startedAt < TimeSpan.FromMilliseconds(Math.Max(0, Recipe.Stage1DurationMs)))
        {
            return false;
        }

        _stage2Applied = true;
        IsComplete = !Recipe.Stage2HoldEnabled;
        nextVoltageV = Recipe.Stage2VoltageV;
        return true;
    }

    public void MarkComplete()
    {
        IsComplete = true;
    }
}
