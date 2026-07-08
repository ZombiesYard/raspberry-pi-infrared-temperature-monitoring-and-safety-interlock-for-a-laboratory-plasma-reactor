using System;

namespace ReactorSoftInterlock.Application.G2000;

public sealed class G2000TelemetrySnapshot
{
    public bool Connected { get; set; }

    public bool CommunicationHealthy { get; set; }

    public DateTimeOffset? LastReceivedAt { get; set; }

    public DateTimeOffset? LastSentAt { get; set; }

    public bool Ready { get; set; }

    public bool Fault { get; set; }

    public bool HvEnable { get; set; }

    public bool HvOn { get; set; }

    public G2000ControlSource Source { get; set; } = G2000ControlSource.Unknown;

    public byte ErrorCode { get; set; }

    public string ErrorText { get; set; } = string.Empty;

    public double? DcLinkVoltageV { get; set; }

    public double? ReservedDcLinkCurrentA { get; set; }

    public double? DcLinkAuxValue { get; set; }

    public double? ReservedOutputVoltageV { get; set; }

    public double? ReservedOutputCurrentA { get; set; }

    public double? FrequencyKhz { get; set; }

    public double? DutyPercent { get; set; }

    public double? TonMs { get; set; }

    public double? ToffMs { get; set; }

    public string StatusFrameHex { get; set; } = string.Empty;

    public string DcLinkActualFrameHex { get; set; } = string.Empty;

    public string InverterActualFrameHex { get; set; } = string.Empty;

    public string ReservedActualFrameHex { get; set; } = string.Empty;

    public string PulseActualFrameHex { get; set; } = string.Empty;

    public G2000HvState TargetHvState { get; set; } = G2000HvState.HvAus;

    public G2000UiMode UiMode { get; set; } = G2000UiMode.Manual;

    public string AutomaticStage { get; set; } = "Idle";

    public bool TripLatched { get; set; }

    public string TripReason { get; set; } = string.Empty;

    public G2000WritableSetpoints TargetSetpoints { get; set; } = new();

    public G2000WritableSetpoints ActualSetpoints { get; set; } = new();

    public G2000TelemetrySnapshot Clone()
    {
        return new G2000TelemetrySnapshot
        {
            Connected = Connected,
            CommunicationHealthy = CommunicationHealthy,
            LastReceivedAt = LastReceivedAt,
            LastSentAt = LastSentAt,
            Ready = Ready,
            Fault = Fault,
            HvEnable = HvEnable,
            HvOn = HvOn,
            Source = Source,
            ErrorCode = ErrorCode,
            ErrorText = ErrorText,
            DcLinkVoltageV = DcLinkVoltageV,
            ReservedDcLinkCurrentA = ReservedDcLinkCurrentA,
            DcLinkAuxValue = DcLinkAuxValue,
            ReservedOutputVoltageV = ReservedOutputVoltageV,
            ReservedOutputCurrentA = ReservedOutputCurrentA,
            FrequencyKhz = FrequencyKhz,
            DutyPercent = DutyPercent,
            TonMs = TonMs,
            ToffMs = ToffMs,
            StatusFrameHex = StatusFrameHex,
            DcLinkActualFrameHex = DcLinkActualFrameHex,
            InverterActualFrameHex = InverterActualFrameHex,
            ReservedActualFrameHex = ReservedActualFrameHex,
            PulseActualFrameHex = PulseActualFrameHex,
            TargetHvState = TargetHvState,
            UiMode = UiMode,
            AutomaticStage = AutomaticStage,
            TripLatched = TripLatched,
            TripReason = TripReason,
            TargetSetpoints = TargetSetpoints.Clone(),
            ActualSetpoints = ActualSetpoints.Clone()
        };
    }
}
