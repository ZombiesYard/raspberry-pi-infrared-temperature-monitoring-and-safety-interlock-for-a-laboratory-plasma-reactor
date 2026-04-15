namespace ReactorSoftInterlock.Application;

public sealed record AutoResetOptions(
    bool Enabled = true,
    double RecoveryThresholdC = 85.0,
    int StableSeconds = 30);
