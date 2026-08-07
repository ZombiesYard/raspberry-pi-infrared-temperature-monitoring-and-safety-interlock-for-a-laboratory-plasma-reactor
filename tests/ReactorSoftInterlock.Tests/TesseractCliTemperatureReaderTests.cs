using System.Diagnostics;
using ReactorSoftInterlock.Application;
using ReactorSoftInterlock.Infrastructure.Capture;
using ReactorSoftInterlock.Infrastructure.Ocr;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class TesseractCliTemperatureReaderTests
{
    [Fact]
    public async Task HungWindowCaptureTimesOutAndDoesNotStartConcurrentCaptures()
    {
        var settings = new AppSettings
        {
            WindowTitleContains = "HikmicroAnalyzer",
            Roi = new RoiSettings { X = 1, Y = 1, Width = 20, Height = 10 },
            Ocr = new OcrSettings { ProcessTimeoutMs = 100 }
        };
        using var releaseCapture = new ManualResetEventSlim();
        var captureReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captureCount = 0;
        var reader = new TesseractCliTemperatureReader(
            (_, _) =>
            {
                Interlocked.Increment(ref captureCount);
                releaseCapture.Wait();
                captureReturned.TrySetResult();
                return new CapturedImage(
                    Path.Combine(Path.GetTempPath(), $"abandoned-capture-{Guid.NewGuid():N}.png"),
                    "roi");
            },
            new TemperatureTextParser(),
            () => settings);
        var stopwatch = Stopwatch.StartNew();

        var first = await reader.ReadAsync(CancellationToken.None);
        var second = await reader.ReadAsync(CancellationToken.None);
        var rebuiltReader = new TesseractCliTemperatureReader(
            (_, _) =>
            {
                Interlocked.Increment(ref captureCount);
                return new CapturedImage("unused.png", "roi");
            },
            new TemperatureTextParser(),
            () => settings);
        var afterReaderRebuild = await rebuiltReader.ReadAsync(CancellationToken.None);

        Assert.Null(first.TemperatureC);
        Assert.Contains("capture exceeded", first.RawText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(second.TemperatureC);
        Assert.Contains("previous HIKMICRO window capture is still running", second.RawText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(afterReaderRebuild.TemperatureC);
        Assert.Contains("previous HIKMICRO window capture is still running", afterReaderRebuild.RawText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, captureCount);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        releaseCapture.Set();
        await captureReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await Task.Delay(25);
    }
}
