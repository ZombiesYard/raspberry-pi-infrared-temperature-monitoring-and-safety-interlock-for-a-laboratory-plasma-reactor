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
    private readonly WindowCapture _windowCapture;
    private readonly TemperatureTextParser _parser;
    private readonly AppSettings _settings;

    public TesseractCliTemperatureReader(WindowCapture windowCapture, TemperatureTextParser parser, AppSettings settings)
    {
        _windowCapture = windowCapture;
        _parser = parser;
        _settings = settings;
    }

    public async Task<TemperatureReading> ReadAsync(CancellationToken cancellationToken)
    {
        CapturedImage? image = null;
        try
        {
            image = _windowCapture.CaptureRoi(_settings.WindowTitleContains, _settings.Roi);
            var rawText = await RunTesseractAsync(image.Path, cancellationToken).ConfigureAwait(false);
            return new TemperatureReading(_parser.ParseHighestTemperatureC(rawText), rawText.Trim(), image.RoiDescription);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new TemperatureReading(null, ex.Message, image?.RoiDescription ?? _settings.Roi.ToString());
        }
        finally
        {
            if (image is not null && File.Exists(image.Path))
            {
                TryDelete(image.Path);
            }
        }
    }

    private async Task<string> RunTesseractAsync(string imagePath, CancellationToken cancellationToken)
    {
        var outputBase = Path.Combine(Path.GetTempPath(), $"reactor-soft-interlock-ocr-{Guid.NewGuid():N}");
        var arguments = $"\"{imagePath}\" \"{outputBase}\" -l {_settings.Ocr.Language} --psm 6";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = RuntimePathResolver.ResolveExecutablePath(_settings.Ocr.TesseractExePath),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };

        process.Start();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1000, _settings.Ocr.ProcessTimeoutMs)));
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
            throw new TimeoutException($"Tesseract OCR exceeded the {_settings.Ocr.ProcessTimeoutMs} ms process timeout.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        var outputPath = outputBase + ".txt";

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Tesseract failed with exit code {process.ExitCode}: {stderr}");
        }

        try
        {
            return File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath, cancellationToken).ConfigureAwait(false) : string.Empty;
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
