namespace ReactorSoftInterlock.Infrastructure.Settings;

public sealed class ExperimentUploadSettings
{
    public bool AutoUploadEnabled { get; set; } = true;

    public string BaseUrl { get; set; } = "https://gitlab.tu-clausthal.de";

    public int ProjectId { get; set; } = 5539;

    public string PackageName { get; set; } = "reactor-experiment-evidence";

    public string CredentialTarget { get; set; } =
        "ReactorSoftInterlock/GitLab/gitlab.tu-clausthal.de/5539";

    public int HttpTimeoutSeconds { get; set; } = 120;

    public void Normalize()
    {
        if (!TryGetSecureBaseUri(BaseUrl, out var parsed) ||
            ProjectId <= 0 ||
            string.IsNullOrWhiteSpace(PackageName))
        {
            AutoUploadEnabled = false;
            BaseUrl = BaseUrl?.Trim() ?? string.Empty;
            PackageName = PackageName?.Trim() ?? string.Empty;
            CredentialTarget = string.Empty;
            HttpTimeoutSeconds = Math.Clamp(HttpTimeoutSeconds, 10, 600);
            return;
        }

        var baseUri = parsed!;
        BaseUrl = baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        PackageName = PackageName.Trim();
        CredentialTarget = BuildCredentialTarget(baseUri, ProjectId);
        HttpTimeoutSeconds = Math.Clamp(HttpTimeoutSeconds, 10, 600);
    }

    public static bool TryGetSecureBaseUri(string? value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(candidate.Host) ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    public static string BuildCredentialTarget(Uri baseUri, int projectId)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        var host = baseUri.IdnHost.ToLowerInvariant();
        var authority = baseUri.IsDefaultPort ? host : $"{host}:{baseUri.Port}";
        return $"ReactorSoftInterlock/GitLab/{authority}/{projectId}";
    }
}
