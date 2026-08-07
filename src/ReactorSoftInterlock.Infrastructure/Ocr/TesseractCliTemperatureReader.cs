using System.Diagnostics;
using System.Runtime.Versioning;
using ReactorSoftInterlock.Application;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Infrastructure.Capture;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Ocr;

[SupportedOSPlatform("windows6.1")]
public sealed class TesseractCliTemperatureReader : ITemperatureReader
{
    private readonly Func<string, RoiSettings, CapturedImage> _captureRoi;
    private readonly TemperatureTextParser _parser;
    private readonly Func<AppSettings> _settingsProvider;
    private static readonly object CaptureSync = new();
    private static Task<CapturedImage>? ActiveCaptureTask;

    public TesseractCliTemperatureReader(WindowCapture windowCapture, TemperatureTextParser parser, AppSettings settings)
        : this(windowCapture, parser, () => settings)
    {
    }

    public TesseractCliTemperatureReader(
        WindowCapture windowCapture,
        TemperatureTextParser parser,
        Func<AppSettings> settingsProvider)
        : this(windowCapture.CaptureRoi, parser, settingsProvider)
    {
    }

    internal TesseractCliTemperatureReader(
        Func<string, RoiSettings, CapturedImage> captureRoi,
        TemperatureTextParser parser,
        Func<AppSettings> settingsProvider)
    {
        _captureRoi = captureRoi;
        _parser = parser;
        _settingsProvider = settingsProvider;
    }

    public async Task<TemperatureReading> ReadAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsProvider();
        var windowTitleContains = settings.WindowTitleContains;
        var roi = new RoiSettings
        {
            X = settings.Roi.X,
            Y = settings.Roi.Y,
            Width = settings.Roi.Width,
            Height = settings.Roi.Height
        };
        var ocr = new OcrSettings
        {
            TesseractExePath = settings.Ocr.TesseractExePath,
            Language = settings.Ocr.Language,
            ProcessTimeoutMs = settings.Ocr.ProcessTimeoutMs
        };
        CapturedImage? image = null;
        try
        {
            image = await CaptureRoiWithTimeoutAsync(
                windowTitleContains,
                roi,
                TimeSpan.FromMilliseconds(Math.Max(100, ocr.ProcessTimeoutMs)),
                cancellationToken).ConfigureAwait(false);
            var rawText = await RunTesseractAsync(image.Path, ocr, cancellationToken).ConfigureAwait(false);
            return new TemperatureReading(_parser.ParseHighestTemperatureC(rawText), rawText.Trim(), image.RoiDescription);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TemperatureReading(null, ex.Message, image?.RoiDescription ?? roi.ToString());
        }
        finally
        {
            if (image is not null && File.Exists(image.Path))
            {
                TryDelete(image.Path);
            }
        }
    }

    private async Task<CapturedImage> CaptureRoiWithTimeoutAsync(
        string windowTitleContains,
        RoiSettings roi,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Task<CapturedImage> captureTask;
        lock (CaptureSync)
        {
            if (ActiveCaptureTask is { IsCompleted: false })
            {
                throw new TimeoutException("The previous HIKMICRO window capture is still running; no second capture was started.");
            }

            if (ActiveCaptureTask is { } completedCapture)
            {
                ActiveCaptureTask = null;
                CleanupCompletedCapture(completedCapture);
            }

            captureTask = Task.Run(() => _captureRoi(windowTitleContains, roi), CancellationToken.None);
            ActiveCaptureTask = captureTask;
        }

        try
        {
            var image = await captureTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            lock (CaptureSync)
            {
                if (ReferenceEquals(ActiveCaptureTask, captureTask))
                {
                    ActiveCaptureTask = null;
                }
            }

            return image;
        }
        catch (TimeoutException)
        {
            ObserveAbandonedCapture(captureTask);
            throw new TimeoutException($"HIKMICRO window capture exceeded the {timeout.TotalMilliseconds:0} ms timeout.");
        }
        catch (OperationCanceledException)
        {
            ObserveAbandonedCapture(captureTask);
            throw;
        }
        catch
        {
            lock (CaptureSync)
            {
                if (ReferenceEquals(ActiveCaptureTask, captureTask))
                {
                    ActiveCaptureTask = null;
                }
            }

            throw;
        }
    }

    private void ObserveAbandonedCapture(Task<CapturedImage> captureTask)
    {
        _ = captureTask.ContinueWith(
            completedCapture =>
            {
                CleanupCompletedCapture(completedCapture);
                lock (CaptureSync)
                {
                    if (ReferenceEquals(ActiveCaptureTask, captureTask))
                    {
                        ActiveCaptureTask = null;
                    }
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void CleanupCompletedCapture(Task<CapturedImage> captureTask)
    {
        if (captureTask.Status == TaskStatus.RanToCompletion)
        {
            TryDelete(captureTask.Result.Path);
        }
        else if (captureTask.IsFaulted)
        {
            _ = captureTask.Exception;
        }
    }

    private static async Task<string> RunTesseractAsync(
        string imagePath,
        OcrSettings settings,
        CancellationToken cancellationToken)
    {
        var outputBase = Path.Combine(Path.GetTempPath(), $"reactor-soft-interlock-ocr-{Guid.NewGuid():N}");
        var outputPath = outputBase + ".txt";
        try
        {
            var arguments = $"\"{imagePath}\" \"{outputBase}\" -l {settings.Language} --psm 6";
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = RuntimePathResolver.ResolveExecutablePath(settings.TesseractExePath),
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            process.Start();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, settings.ProcessTimeoutMs)));
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            string stderr;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException($"Tesseract OCR exceeded the {settings.ProcessTimeoutMs} ms process timeout.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Tesseract failed with exit code {process.ExitCode}: {stderr}");
            }

            return File.Exists(outputPath)
                ? await File.ReadAllTextAsync(outputPath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Cancellation and timeout must still return control even if process cleanup fails.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Temporary OCR files are best-effort cleanup.
        }
    }
}
