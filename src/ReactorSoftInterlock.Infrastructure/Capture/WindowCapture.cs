using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Infrastructure.Capture;

[SupportedOSPlatform("windows6.1")]
public sealed class WindowCapture
{
    public WindowBounds GetWindowBounds(string titleContains)
    {
        var handle = FindWindow(titleContains);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not find a visible window containing title text '{titleContains}'.");
        }

        if (!GetWindowRect(handle, out var rect))
        {
            throw new InvalidOperationException("Could not read HikmicroAnalyzer window bounds.");
        }

        return new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public CapturedImage CaptureRoi(string titleContains, RoiSettings roi)
    {
        if (!roi.IsConfigured)
        {
            throw new InvalidOperationException("ROI is not configured. Use Select ROI before starting monitoring.");
        }

        var rect = GetWindowBounds(titleContains);

        var x = rect.Left + roi.X;
        var y = rect.Top + roi.Y;
        using var bitmap = new Bitmap(roi.Width, roi.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(x, y, 0, 0, new Size(roi.Width, roi.Height), CopyPixelOperation.SourceCopy);

        var path = Path.Combine(Path.GetTempPath(), $"reactor-soft-interlock-ocr-{Guid.NewGuid():N}.png");
        bitmap.Save(path, ImageFormat.Png);
        return new CapturedImage(path, $"{titleContains}:{roi}");
    }

    private static IntPtr FindWindow(string titleContains)
    {
        var comparison = StringComparison.OrdinalIgnoreCase;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero &&
                    process.MainWindowTitle.Contains(titleContains, comparison))
                {
                    return process.MainWindowHandle;
                }
            }
            catch
            {
                // Process metadata can disappear while enumerating windows.
            }
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
