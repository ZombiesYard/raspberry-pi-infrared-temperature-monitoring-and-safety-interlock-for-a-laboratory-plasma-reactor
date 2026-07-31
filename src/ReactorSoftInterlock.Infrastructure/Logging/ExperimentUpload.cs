using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Logging;

public enum ExperimentUploadState
{
    Disabled,
    MissingCredential,
    Packaging,
    Pending,
    Uploading,
    Uploaded,
    Failed
}

public interface IExperimentCredentialStore
{
    Task<string?> ReadTokenAsync(string target, CancellationToken cancellationToken);
    Task StoreTokenAsync(string target, string value, CancellationToken cancellationToken);
    Task ClearAsync(string target, CancellationToken cancellationToken);
}

public interface IExperimentPackageUploader
{
    Task<ExperimentUploadReceipt> UploadAsync(
        ExperimentUploadWorkItem item,
        ExperimentUploadSettings settings,
        CancellationToken cancellationToken);
}

public sealed class ExperimentCredentialMissingException : InvalidOperationException
{
    public ExperimentCredentialMissingException()
        : base("The GitLab credential is missing from Windows Credential Manager.")
    {
    }
}

public sealed class ExperimentUploadException : HttpRequestException
{
    public ExperimentUploadException(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
        : base($"GitLab experiment upload failed with HTTP status {(int)statusCode}.", null, statusCode)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}

public sealed class ExperimentLocalIntegrityException : IOException
{
    public ExperimentLocalIntegrityException()
        : base("The pending experiment bundle no longer matches its recorded size and SHA-256.")
    {
    }
}

public sealed class ExperimentUploadWorkItem
{
    public string SessionId { get; set; } = string.Empty;
    public string BundlePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string SessionDirectory { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string LastFailureKind { get; set; } = string.Empty;
    public string TargetBaseUrl { get; set; } = string.Empty;
    public int TargetProjectId { get; set; }
    public string TargetPackageName { get; set; } = string.Empty;
    public string TargetCredentialName { get; set; } = string.Empty;

    public static async Task<ExperimentUploadWorkItem> CreateAsync(
        string sessionId,
        string bundlePath,
        CancellationToken cancellationToken,
        string? sessionDirectory = null,
        ExperimentUploadSettings? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        var fullPath = Path.GetFullPath(bundlePath);
        await using var stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        target ??= new ExperimentUploadSettings();
        target.Normalize();
        return new ExperimentUploadWorkItem
        {
            SessionId = sessionId,
            BundlePath = fullPath,
            FileName = Path.GetFileName(fullPath),
            SizeBytes = stream.Length,
            Sha256 = Convert.ToHexString(hash).ToLowerInvariant(),
            SessionDirectory = sessionDirectory ?? string.Empty,
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
            TargetBaseUrl = target.BaseUrl,
            TargetProjectId = target.ProjectId,
            TargetPackageName = target.PackageName,
            TargetCredentialName = target.CredentialTarget
        };
    }

    public ExperimentUploadSettings ResolveTarget(ExperimentUploadSettings fallback)
    {
        var baseUrl = string.IsNullOrWhiteSpace(TargetBaseUrl) ? fallback.BaseUrl : TargetBaseUrl;
        if (!ExperimentUploadSettings.TryGetSecureBaseUri(baseUrl, out _))
        {
            throw new InvalidOperationException("GitLab experiment upload requires a secure HTTPS base URL.");
        }

        var target = new ExperimentUploadSettings
        {
            AutoUploadEnabled = true,
            BaseUrl = baseUrl,
            ProjectId = TargetProjectId > 0 ? TargetProjectId : fallback.ProjectId,
            PackageName = string.IsNullOrWhiteSpace(TargetPackageName)
                ? fallback.PackageName
                : TargetPackageName,
            HttpTimeoutSeconds = fallback.HttpTimeoutSeconds
        };
        target.Normalize();
        return target;
    }
}

public sealed class ExperimentUploadReceipt
{
    public string SessionId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool AlreadyPresent { get; set; }
    public DateTimeOffset UploadedAtUtc { get; set; }
    public string RemoteUrl { get; set; } = string.Empty;
}

public sealed class GitLabExperimentUploader : IExperimentPackageUploader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IExperimentCredentialStore _credentialStore;

    public GitLabExperimentUploader(
        HttpClient httpClient,
        IExperimentCredentialStore credentialStore)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    }

    public async Task<ExperimentUploadReceipt> UploadAsync(
        ExperimentUploadWorkItem item,
        ExperimentUploadSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);
        var target = item.ResolveTarget(settings);
        if (!ExperimentUploadSettings.TryGetSecureBaseUri(target.BaseUrl, out _))
        {
            throw new InvalidOperationException("GitLab experiment upload requires a secure HTTPS base URL.");
        }

        if (!File.Exists(item.BundlePath))
        {
            throw new FileNotFoundException("The pending experiment bundle is missing.", item.FileName);
        }

        await using var bundleStream = await OpenVerifiedBundleAsync(item, cancellationToken)
            .ConfigureAwait(false);

        var token = await _credentialStore.ReadTokenAsync(target.CredentialTarget, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ExperimentCredentialMissingException();
        }

        if (await RemoteFileMatchesAsync(item, target, token, cancellationToken).ConfigureAwait(false))
        {
            return CreateReceipt(item, target, alreadyPresent: true);
        }

        var uploadUrl = BuildGenericPackageUrl(target, item.SessionId, item.FileName);
        using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl);
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        bundleStream.Position = 0;
        request.Content = new StreamContent(bundleStream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        request.Content.Headers.ContentLength = item.SizeBytes;
        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateUploadException(response);
        }

        return CreateReceipt(item, target, alreadyPresent: false);
    }

    private async Task<bool> RemoteFileMatchesAsync(
        ExperimentUploadWorkItem item,
        ExperimentUploadSettings settings,
        string token,
        CancellationToken cancellationToken)
    {
        var packagesUrl =
            $"{settings.BaseUrl}/api/v4/projects/{settings.ProjectId}/packages" +
            $"?package_type=generic&package_name={Encode(settings.PackageName)}" +
            $"&package_version={Encode(item.SessionId)}&per_page=100";
        using var packageRequest = new HttpRequestMessage(HttpMethod.Get, packagesUrl);
        packageRequest.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        using var packageResponse = await _httpClient.SendAsync(
            packageRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (packageResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!packageResponse.IsSuccessStatusCode)
        {
            throw CreateUploadException(packageResponse);
        }

        await using var packageStream = await packageResponse.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var packages = await JsonSerializer.DeserializeAsync<List<GitLabPackage>>(
            packageStream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
        var package = packages.FirstOrDefault(value =>
            string.Equals(value.Version, item.SessionId, StringComparison.Ordinal));
        if (package is null)
        {
            return false;
        }

        var filesUrl = $"{settings.BaseUrl}/api/v4/projects/{settings.ProjectId}/packages/{package.Id}/package_files?per_page=100";
        using var filesRequest = new HttpRequestMessage(HttpMethod.Get, filesUrl);
        filesRequest.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", token);
        using var filesResponse = await _httpClient.SendAsync(
            filesRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!filesResponse.IsSuccessStatusCode)
        {
            throw CreateUploadException(filesResponse);
        }

        await using var filesStream = await filesResponse.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var files = await JsonSerializer.DeserializeAsync<List<GitLabPackageFile>>(
            filesStream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
        return files.Any(file =>
            string.Equals(file.FileName, item.FileName, StringComparison.Ordinal) &&
            file.Size == item.SizeBytes &&
            string.Equals(file.FileSha256, item.Sha256, StringComparison.OrdinalIgnoreCase));
    }

    private static ExperimentUploadReceipt CreateReceipt(
        ExperimentUploadWorkItem item,
        ExperimentUploadSettings settings,
        bool alreadyPresent) => new()
        {
            SessionId = item.SessionId,
            FileName = item.FileName,
            SizeBytes = item.SizeBytes,
            Sha256 = item.Sha256,
            AlreadyPresent = alreadyPresent,
            UploadedAtUtc = DateTimeOffset.UtcNow,
            RemoteUrl = BuildGenericPackageUrl(settings, item.SessionId, item.FileName)
        };

    internal static string BuildGenericPackageUrl(
        ExperimentUploadSettings settings,
        string sessionId,
        string fileName) =>
        $"{settings.BaseUrl.TrimEnd('/')}/api/v4/projects/{settings.ProjectId}/packages/generic/" +
        $"{Encode(settings.PackageName)}/{Encode(sessionId)}/{Encode(fileName)}";

    private static string Encode(string value) => Uri.EscapeDataString(value);

    private static async Task<FileStream> OpenVerifiedBundleAsync(
        ExperimentUploadWorkItem item,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            item.BundlePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            if (stream.Length != item.SizeBytes)
            {
                throw new ExperimentLocalIntegrityException();
            }

            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)).ToLowerInvariant();
            if (!string.Equals(hash, item.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new ExperimentLocalIntegrityException();
            }

            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ExperimentUploadException CreateUploadException(HttpResponseMessage response)
    {
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } retryDate)
        {
            retryAfter = retryDate - DateTimeOffset.UtcNow;
        }

        return new ExperimentUploadException(
            response.StatusCode,
            retryAfter > TimeSpan.Zero ? retryAfter : null);
    }

    private sealed class GitLabPackage
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;
    }

    private sealed class GitLabPackageFile
    {
        [JsonPropertyName("file_name")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("file_sha256")]
        public string FileSha256 { get; set; } = string.Empty;
    }
}
