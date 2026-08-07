using System.Text.Json;

namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SettingsStore(string path)
    {
        _path = path;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            var defaults = new AppSettings();
            defaults.Relay.Normalize();
            defaults.Amc2100.Normalize();
            defaults.ExperimentUpload.Normalize();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        await using var stream = File.OpenRead(_path);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? new AppSettings();
        settings.Relay.Normalize();
        settings.Amc2100.Normalize();
        settings.ExperimentUpload ??= new ExperimentUploadSettings();
        settings.ExperimentUpload.Normalize();
        return settings;
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            settings.Relay.Normalize();
            settings.Amc2100.Normalize();
            settings.ExperimentUpload ??= new ExperimentUploadSettings();
            settings.ExperimentUpload.Normalize();
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
