using ReactorSoftInterlock.Application.G2000;

namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class G2000CanSettings
{
    public string Channel { get; set; } = "UsbBus1";

    public byte NodeId { get; set; } = 0;

    public int CommandPeriodMs { get; set; } = 100;

    public int ReadPollIntervalMs { get; set; } = 16;

    public string UiMode { get; set; } = nameof(G2000UiMode.Manual);

    public string RecoveryPolicy { get; set; } = nameof(TripRecoveryPolicy.HoldHvAus);

    public double VerifiedU2MinVoltageV { get; set; } = 0.0;

    public double VerifiedU2MaxVoltageV { get; set; } = 300.0;

    public bool AllowUnsafeU2Writes { get; set; }

    public int HvReadyLeadTimeMs { get; set; } = 2000;

    public G2000WritableSetpoints WritableSetpoints { get; set; } = new();

    public G2000StartupRecipe StartupRecipe { get; set; } = new();

    public G2000CanSettings Clone()
    {
        return new G2000CanSettings
        {
            Channel = Channel,
            NodeId = NodeId,
            CommandPeriodMs = CommandPeriodMs,
            ReadPollIntervalMs = ReadPollIntervalMs,
            UiMode = UiMode,
            RecoveryPolicy = RecoveryPolicy,
            VerifiedU2MinVoltageV = VerifiedU2MinVoltageV,
            VerifiedU2MaxVoltageV = VerifiedU2MaxVoltageV,
            AllowUnsafeU2Writes = AllowUnsafeU2Writes,
            HvReadyLeadTimeMs = HvReadyLeadTimeMs,
            WritableSetpoints = WritableSetpoints?.Clone() ?? new G2000WritableSetpoints(),
            StartupRecipe = StartupRecipe?.Clone() ?? new G2000StartupRecipe()
        };
    }

    public void Normalize()
    {
        if (CommandPeriodMs <= 0)
        {
            CommandPeriodMs = 100;
        }

        if (ReadPollIntervalMs <= 0)
        {
            ReadPollIntervalMs = 16;
        }

        if (HvReadyLeadTimeMs < 0)
        {
            HvReadyLeadTimeMs = 2000;
        }

        if (VerifiedU2MinVoltageV < 0)
        {
            VerifiedU2MinVoltageV = 0.0;
        }

        if (VerifiedU2MaxVoltageV < VerifiedU2MinVoltageV)
        {
            VerifiedU2MaxVoltageV = VerifiedU2MinVoltageV;
        }

        WritableSetpoints ??= new G2000WritableSetpoints();
        StartupRecipe ??= new G2000StartupRecipe();
    }

    public void ValidateWritableSetpoints(G2000WritableSetpoints setpoints)
    {
        ArgumentNullException.ThrowIfNull(setpoints);

        ValidateU2Voltage(setpoints.VoltageV, nameof(setpoints.VoltageV));
    }

    public void ValidateStartupRecipe(G2000StartupRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        ValidateU2Voltage(recipe.Stage1VoltageV, nameof(recipe.Stage1VoltageV));
        ValidateU2Voltage(recipe.Stage2VoltageV, nameof(recipe.Stage2VoltageV));
    }

    public string DescribeVerifiedU2Window()
    {
        return $"{VerifiedU2MinVoltageV:0.###}-{VerifiedU2MaxVoltageV:0.###} V (U2)";
    }

    private void ValidateU2Voltage(double value, string name)
    {
        if (AllowUnsafeU2Writes)
        {
            return;
        }

        if (value < VerifiedU2MinVoltageV || value > VerifiedU2MaxVoltageV)
        {
            throw new InvalidOperationException(
                $"{name}={value:0.###} V is outside the configured U2 range {DescribeVerifiedU2Window()}. " +
                " The G2000 manual defines 0x300 as the internal DC-link setpoint U2, not the external HV output voltage. " +
                "Out-of-range U2 writes are blocked unless AllowUnsafeU2Writes is enabled.");
        }
    }

    public G2000UiMode ResolveUiMode()
    {
        return Enum.TryParse<G2000UiMode>(UiMode, ignoreCase: true, out var mode)
            ? mode
            : G2000UiMode.Manual;
    }

    public TripRecoveryPolicy ResolveRecoveryPolicy()
    {
        return Enum.TryParse<TripRecoveryPolicy>(RecoveryPolicy, ignoreCase: true, out var policy)
            ? policy
            : TripRecoveryPolicy.HoldHvAus;
    }
}
