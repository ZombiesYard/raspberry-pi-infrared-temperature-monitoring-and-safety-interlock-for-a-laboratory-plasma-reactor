using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReactorSoftInterlock.Infrastructure.Logging;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class GitLabExperimentUploaderTests
{
    [Fact]
    public async Task UploadAsync_UsesEncodedGenericPackagePathAndPrivateTokenHeader()
    {
        var root = CreateTemporaryDirectory();
        var bundlePath = Path.Combine(root, "exp-short.zip");
        await File.WriteAllTextAsync(bundlePath, "bundle-body");
        var handler = new RecordingHandler(
            _ => JsonResponse("[]"),
            _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") });
        using var client = new HttpClient(handler);
        var uploader = new GitLabExperimentUploader(
            client, new FakeCredentialStore("pat-value-never-persist"));
        var settings = new ExperimentUploadSettings
        {
            BaseUrl = "https://gitlab.example.test/",
            ProjectId = 5539,
            PackageName = "reactor evidence",
            CredentialTarget = "ReactorSoftInterlock/GitLab/5539"
        };
        var item = await ExperimentUploadWorkItem.CreateAsync(
            "run id/1", bundlePath, CancellationToken.None, target: settings);

        var receipt = await uploader.UploadAsync(item, settings, CancellationToken.None);

        Assert.False(receipt.AlreadyPresent);
        Assert.Equal(HttpMethod.Put, handler.UploadRequest!.Method);
        Assert.Equal(
            "/api/v4/projects/5539/packages/generic/reactor%20evidence/run%20id%2F1/exp-short.zip",
            handler.UploadRequest.RequestUri!.AbsolutePath);
        Assert.Equal("pat-value-never-persist", handler.UploadToken);
        Assert.Equal(item.Sha256, receipt.Sha256);
        Assert.Equal(item.SizeBytes, receipt.SizeBytes);
    }

    [Fact]
    public async Task UploadAsync_SkipsPutWhenRemotePackageFileMatchesNameSizeAndSha256()
    {
        var root = CreateTemporaryDirectory();
        var bundlePath = Path.Combine(root, "exp.zip");
        await File.WriteAllTextAsync(bundlePath, "same-body");
        var item = await ExperimentUploadWorkItem.CreateAsync("run-1", bundlePath, CancellationToken.None);
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/packages", StringComparison.Ordinal))
            {
                return JsonResponse("[{\"id\":42,\"version\":\"run-1\"}]");
            }

            return JsonResponse($"[{{\"file_name\":\"exp.zip\",\"size\":{item.SizeBytes},\"file_sha256\":\"{item.Sha256}\"}}]");
        });
        using var client = new HttpClient(handler);
        var uploader = new GitLabExperimentUploader(client, new FakeCredentialStore("token"));

        var receipt = await uploader.UploadAsync(
            item,
            new ExperimentUploadSettings { BaseUrl = "https://gitlab.example.test", ProjectId = 5539 },
            CancellationToken.None);

        Assert.True(receipt.AlreadyPresent);
        Assert.Null(handler.UploadRequest);
    }

    [Fact]
    public async Task UploadAsync_MissingCredentialDoesNotSendRequestOrExposeToken()
    {
        var root = CreateTemporaryDirectory();
        var bundlePath = Path.Combine(root, "exp.zip");
        await File.WriteAllTextAsync(bundlePath, "body");
        var item = await ExperimentUploadWorkItem.CreateAsync("run", bundlePath, CancellationToken.None);
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        using var client = new HttpClient(handler);
        var uploader = new GitLabExperimentUploader(client, new FakeCredentialStore(null));

        var exception = await Assert.ThrowsAsync<ExperimentCredentialMissingException>(() =>
            uploader.UploadAsync(item, new ExperimentUploadSettings(), CancellationToken.None));

        Assert.DoesNotContain("token", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WorkItemContainsStableSha256AndNeverSerializesCredential()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "exp.zip");
        var bytes = Encoding.UTF8.GetBytes("evidence");
        await File.WriteAllBytesAsync(path, bytes);

        var item = await ExperimentUploadWorkItem.CreateAsync("run", path, CancellationToken.None);
        var json = JsonSerializer.Serialize(item);

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), item.Sha256);
        Assert.Equal(bytes.Length, item.SizeBytes);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pat-value", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UploadAsync_UnauthorizedResponseDoesNotIncludeServerBodyOrCredential()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "exp.zip");
        await File.WriteAllTextAsync(path, "body");
        var item = await ExperimentUploadWorkItem.CreateAsync("run", path, CancellationToken.None);
        var handler = new RecordingHandler(
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("server echoed super-secret-token")
            });
        using var client = new HttpClient(handler);
        var uploader = new GitLabExperimentUploader(
            client, new FakeCredentialStore("super-secret-token"));

        var exception = await Assert.ThrowsAsync<ExperimentUploadException>(() =>
            uploader.UploadAsync(item, new ExperimentUploadSettings(), CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.DoesNotContain("super-secret-token", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("server echoed", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task UploadAsync_ServerFailuresReturnSanitizedStatusOnly(HttpStatusCode statusCode)
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "exp.zip");
        await File.WriteAllTextAsync(path, "body");
        var item = await ExperimentUploadWorkItem.CreateAsync("run", path, CancellationToken.None);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent("untrusted response body")
        });
        using var client = new HttpClient(handler);
        var uploader = new GitLabExperimentUploader(client, new FakeCredentialStore("secret"));

        var exception = await Assert.ThrowsAsync<ExperimentUploadException>(() =>
            uploader.UploadAsync(item, new ExperimentUploadSettings(), CancellationToken.None));

        Assert.Equal(statusCode, exception.StatusCode);
        Assert.DoesNotContain("untrusted response body", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadAsync_TimeoutDoesNotExposeCredential()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "exp.zip");
        await File.WriteAllTextAsync(path, "body");
        var item = await ExperimentUploadWorkItem.CreateAsync("run", path, CancellationToken.None);
        var handler = new RecordingHandler(_ => throw new TaskCanceledException("simulated timeout"));
        using var client = new HttpClient(handler);
        var uploader = new GitLabExperimentUploader(
            client, new FakeCredentialStore("timeout-secret"));

        var exception = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            uploader.UploadAsync(item, new ExperimentUploadSettings(), CancellationToken.None));

        Assert.DoesNotContain("timeout-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadAsync_RateLimitPreservesRetryAfterWithoutResponseBody()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "exp.zip");
        await File.WriteAllTextAsync(path, "body");
        var item = await ExperimentUploadWorkItem.CreateAsync("run", path, CancellationToken.None);
        var handler = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("do not persist this body")
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(47));
            return response;
        });
        using var client = new HttpClient(handler);
        var uploader = new GitLabExperimentUploader(client, new FakeCredentialStore("secret"));

        var exception = await Assert.ThrowsAsync<ExperimentUploadException>(() =>
            uploader.UploadAsync(item, new ExperimentUploadSettings(), CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(47), exception.RetryAfter);
        Assert.DoesNotContain("do not persist", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadAsync_MissingLocalBundleFailsBeforeCredentialOrNetworkUse()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "exp.zip");
        await File.WriteAllTextAsync(path, "body");
        var item = await ExperimentUploadWorkItem.CreateAsync("run", path, CancellationToken.None);
        File.Delete(path);
        var credentials = new CountingCredentialStore();
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        using var client = new HttpClient(handler);
        var uploader = new GitLabExperimentUploader(client, credentials);

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            uploader.UploadAsync(item, new ExperimentUploadSettings(), CancellationToken.None));

        Assert.Equal(0, credentials.ReadCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UploadAsync_RejectsHttpBeforeCredentialOrNetworkUse()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "exp.zip");
        await File.WriteAllTextAsync(path, "body");
        var item = await ExperimentUploadWorkItem.CreateAsync(
            "run",
            path,
            CancellationToken.None,
            target: new ExperimentUploadSettings { BaseUrl = "https://safe.example.test" });
        item.TargetBaseUrl = "http://unsafe.example.test";
        var credentials = new CountingCredentialStore();
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        using var client = new HttpClient(handler);
        var uploader = new GitLabExperimentUploader(client, credentials);

        await Assert.ThrowsAsync<InvalidOperationException>(() => uploader.UploadAsync(
            item, new ExperimentUploadSettings(), CancellationToken.None));

        Assert.Equal(0, credentials.ReadCount);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("tampered-longer")]
    [InlineData("other-body")]
    public async Task UploadAsync_RejectsChangedBundleBeforeCredentialOrNetworkUse(string replacement)
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "exp.zip");
        await File.WriteAllTextAsync(path, "valid-body");
        var item = await ExperimentUploadWorkItem.CreateAsync("run", path, CancellationToken.None);
        await File.WriteAllTextAsync(path, replacement);
        var credentials = new CountingCredentialStore();
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("must not send"));
        using var client = new HttpClient(handler);
        var uploader = new GitLabExperimentUploader(client, credentials);

        await Assert.ThrowsAsync<ExperimentLocalIntegrityException>(() => uploader.UploadAsync(
            item, new ExperimentUploadSettings(), CancellationToken.None));

        Assert.Equal(0, credentials.ReadCount);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UploadAsync_UsesWorkItemTargetAfterApplicationSettingsChange()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "exp.zip");
        await File.WriteAllTextAsync(path, "body");
        var original = new ExperimentUploadSettings
        {
            BaseUrl = "https://original.example.test",
            ProjectId = 11,
            PackageName = "original-package"
        };
        var item = await ExperimentUploadWorkItem.CreateAsync(
            "run", path, CancellationToken.None, target: original);
        var credentials = new CountingCredentialStore();
        var handler = new RecordingHandler(_ => JsonResponse("[]"), _ =>
            new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new HttpClient(handler);
        var uploader = new GitLabExperimentUploader(client, credentials);

        await uploader.UploadAsync(
            item,
            new ExperimentUploadSettings
            {
                BaseUrl = "https://changed.example.test",
                ProjectId = 99,
                PackageName = "changed-package"
            },
            CancellationToken.None);

        Assert.All(handler.Requests, request =>
            Assert.Equal("original.example.test", request.RequestUri!.Host));
        Assert.Equal(
            "ReactorSoftInterlock/GitLab/original.example.test/11",
            credentials.LastTarget);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FakeCredentialStore(string? token) : IExperimentCredentialStore
    {
        public Task<string?> ReadTokenAsync(string target, CancellationToken cancellationToken) =>
            Task.FromResult(token);

        public Task StoreTokenAsync(string target, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ClearAsync(string target, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CountingCredentialStore : IExperimentCredentialStore
    {
        public int ReadCount { get; private set; }
        public string? LastTarget { get; private set; }

        public Task<string?> ReadTokenAsync(string target, CancellationToken cancellationToken)
        {
            ReadCount++;
            LastTarget = target;
            return Task.FromResult<string?>("secret");
        }

        public Task StoreTokenAsync(string target, string value, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ClearAsync(string target, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _getResponse;
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _uploadResponse;

        public RecordingHandler(
            Func<HttpRequestMessage, HttpResponseMessage> getResponse,
            Func<HttpRequestMessage, HttpResponseMessage>? uploadResponse = null)
        {
            _getResponse = getResponse;
            _uploadResponse = uploadResponse ?? (_ => throw new InvalidOperationException("Unexpected PUT"));
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public HttpRequestMessage? UploadRequest { get; private set; }
        public string? UploadToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Method == HttpMethod.Put)
            {
                UploadRequest = request;
                UploadToken = request.Headers.TryGetValues("PRIVATE-TOKEN", out var values)
                    ? values.Single()
                    : null;
                return Task.FromResult(_uploadResponse(request));
            }

            return Task.FromResult(_getResponse(request));
        }
    }
}
