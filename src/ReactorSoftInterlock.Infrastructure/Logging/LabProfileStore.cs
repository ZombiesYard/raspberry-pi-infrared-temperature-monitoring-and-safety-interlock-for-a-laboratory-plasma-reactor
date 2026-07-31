using System.Text;
using System.Text.Json;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Logging;

public sealed class LabProfile
{
    public string SchemaVersion { get; set; } = "1";
    public string Operator { get; set; } = "Yu Zhang";
    public List<string> Supervisors { get; set; } = ["M.Sc. Shukang Zhang", "Denis Kruschinski, M.Sc."];
    public CameraProfile Camera { get; set; } = new();
    public RelayProfile Relay { get; set; } = new();
    public ReactorProfile Reactor { get; set; } = new();
    public CalibrationProfile Calibration { get; set; } = new();
    public ProcessProfile Process { get; set; } = new();
    public EnvironmentProfile Environment { get; set; } = new();
    public EquipmentProfile Equipment { get; set; } = new();
}

public sealed class CameraProfile
{
    public string Model { get; set; } = "HIKMICRO E20Plus";
    public double DistanceM { get; set; } = 0.40;
    public string MeasurementSource { get; set; } = "HikmicroAnalyzer Max overlay";
    public string Emissivity { get; set; } = "not_recorded";
    public string ReflectedTemperature { get; set; } = "not_recorded";
    public string SerialNumber { get; set; } = "not_recorded";
    public string ViewingAngle { get; set; } = "not_recorded";
}

public sealed class RelayProfile
{
    public string Model { get; set; } = "DSD TECH SH-UR04A 4CH";
    public string InterlockContactMode { get; set; } = "normally_closed";
    public Dictionary<string, string> ChannelMappings { get; set; } = new()
    {
        ["CH1"] = "I1-I2",
        ["CH2"] = "I5-I6",
        ["CH3"] = "I3-I4",
        ["CH4"] = "I7-I8"
    };
    public string SerialNumber { get; set; } = "not_recorded";
}

public sealed class ReactorProfile
{
    public string PlasmaContactBaseMaterial { get; set; } = "ceramic";
    public double HistoricalEmpiricalFailureTemperatureC { get; set; } = 195.0;
    public double HighestObservedTemperatureC { get; set; } = 160.0;
    public string HistoricalDamageMechanism { get; set; } = "not_confirmed";
    public string DetailedMaterialsAndDimensions { get; set; } = "not_recorded";
    public string Adhesive { get; set; } = "not_recorded";
    public string HistoricalDamageEvidence { get; set; } = "not_recorded";
}

public sealed class CalibrationProfile
{
    public string ReferenceThermometer { get; set; } = "not_recorded";
    public string CalibrationData { get; set; } = "not_recorded";
    public string CameraCalibrationDate { get; set; } = "not_recorded";
}

public sealed class ProcessProfile
{
    public string GasSpecies { get; set; } = "not_recorded";
    public string GasSupplyConditions { get; set; } = "not_recorded";
}

public sealed class EnvironmentProfile
{
    public string AmbientTemperature { get; set; } = "not_recorded";
    public string Humidity { get; set; } = "not_recorded";
    public string LightingAndReflectiveSurfaces { get; set; } = "not_recorded";
}

public sealed class EquipmentProfile
{
    public string G2000ModelAndSerial { get; set; } = "not_recorded";
    public string G2000Firmware { get; set; } = "not_recorded";
    public string CanAdapterAndTermination { get; set; } = "not_recorded";
    public string Amc2100ModelAndSerial { get; set; } = "not_recorded";
    public string Amc2100RangeAndCalibration { get; set; } = "not_recorded";
    public string SetupAndWiringPhotos { get; set; } = "not_recorded";
}

public static class LabProfileStore
{
    public const string FileName = "lab-profile.json";
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<LabProfile> LoadOrCreateAsync(
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path))
        {
            var defaults = new LabProfile();
            await SaveAsync(dataDirectory, defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<LabProfile>(stream, JsonOptions, cancellationToken)
                   .ConfigureAwait(false)
               ?? new LabProfile();
    }

    public static async Task SaveAsync(
        string dataDirectory,
        LabProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(profile);
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(profile, JsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, Utf8WithoutBom, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    internal static string Serialize(LabProfile profile) =>
        JsonSerializer.Serialize(profile, JsonOptions);

    internal static string RenderExperimentContext(
        LabProfile profile,
        AppSettings settings,
        string sessionId,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        string finalizationReason)
    {
        var roi = settings.Roi;
        var recipe = settings.Relay.G2000Can.StartupRecipe;
        return $"""
            # Experiment context

            This file is generated automatically from the persistent lab profile and the run settings snapshot.

            ## Run

            - Run ID: {sessionId}
            - Started (UTC): {startedAt:O}
            - Ended (UTC): {(endedAt.HasValue ? endedAt.Value.ToString("O") : "active")}
            - Finalization reason: {(string.IsNullOrWhiteSpace(finalizationReason) ? "active" : finalizationReason)}
            - Trip threshold: {settings.ThresholdC:G17} °C
            - Recovery: {(settings.AutoResetEnabled ? "automatic" : "manual")}, {settings.RecoveryThresholdC:G17} °C for {settings.RecoveryStableSeconds} s
            - OCR ROI: X={roi.X}, Y={roi.Y}, Width={roi.Width}, Height={roi.Height}
            - G2000 recipe: stage 1 {recipe.Stage1VoltageV:G17} V for {recipe.Stage1DurationMs} ms; stage 2 {recipe.Stage2VoltageV:G17} V; hold={recipe.Stage2HoldEnabled}
            - AMC2100 enabled: {settings.Amc2100.Enabled}

            ## Fixed laboratory profile

            - Operator: {profile.Operator}
            - Supervisors: {string.Join("; ", profile.Supervisors)}
            - Camera: {profile.Camera.Model}, approximately {profile.Camera.DistanceM:G2} m, {profile.Camera.MeasurementSource}
            - Camera emissivity: {profile.Camera.Emissivity}
            - Reflected temperature: {profile.Camera.ReflectedTemperature}
            - Relay: {profile.Relay.Model}, {profile.Relay.InterlockContactMode}
            - Relay mapping: {string.Join("; ", profile.Relay.ChannelMappings.Select(pair => $"{pair.Key}→{pair.Value}"))}
            - Reactor plasma-contact base: {profile.Reactor.PlasmaContactBaseMaterial}
            - Historical empirical temperature: approximately {profile.Reactor.HistoricalEmpiricalFailureTemperatureC:G17} °C
            - Highest observed temperature: {profile.Reactor.HighestObservedTemperatureC:G17} °C
            - Historical damage mechanism: {profile.Reactor.HistoricalDamageMechanism}
            - Gas species: {profile.Process.GasSpecies}
            - Reference thermometer: {profile.Calibration.ReferenceThermometer}
            - Calibration data: {profile.Calibration.CalibrationData}
            - Ambient conditions: temperature={profile.Environment.AmbientTemperature}; humidity={profile.Environment.Humidity}
            - Equipment serial numbers and setup photographs: G2000={profile.Equipment.G2000ModelAndSerial}; AMC2100={profile.Equipment.Amc2100ModelAndSerial}; photographs={profile.Equipment.SetupAndWiringPhotos}

            Values marked `not_recorded` or `not_confirmed` remain explicit evidence gaps and must not be inferred from software logs.
            """;
    }
}
