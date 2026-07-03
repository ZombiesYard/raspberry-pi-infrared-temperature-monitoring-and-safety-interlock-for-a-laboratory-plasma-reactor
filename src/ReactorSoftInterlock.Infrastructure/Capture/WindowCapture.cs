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

        RestoreIfMinimized(handle);
        if (!GetWindowRect(handle, out var rect))
        {
            throw new InvalidOperationException("Could not read HikmicroAnalyzer window bounds.");
        }

        return new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    public bool BringWindowToForeground(string titleContains)
    {
        var handle = FindWindow(titleContains);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not find a visible window containing title text '{titleContains}'.");
        }

        RestoreIfMinimized(handle);
        return SetForegroundWindow(handle);
    }

    public CapturedImage CaptureRoi(string titleContains, RoiSettings roi)
    {
        if (!roi.IsConfigured)
        {
            throw new InvalidOperationException("ROI is not configured. Use Select ROI before starting monitoring.");
        }

        var handle = FindWindow(titleContains);
        if (handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Could not find a visible window containing title text '{titleContains}'.");
        }

        RestoreIfMinimized(handle);
        if (!GetWindowRect(handle, out var rect))
        {
            throw new InvalidOperationException("Could not read HikmicroAnalyzer window bounds.");
        }

        var bounds = new WindowBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var captureRectangle = CalculateCaptureRectangle(roi, bounds.Width, bounds.Height);
        using var bitmap = CaptureForegroundScreenRoi(handle, bounds, captureRectangle)
                           ?? CaptureWindowRoi(handle, captureRectangle, bounds.Width, bounds.Height)
                           ?? CaptureScreenRoiAfterForegroundRestore(handle, bounds, captureRectangle);

        var path = Path.Combine(Path.GetTempPath(), $"reactor-soft-interlock-ocr-{Guid.NewGuid():N}.png");
        bitmap.Save(path, ImageFormat.Png);
        return new CapturedImage(path, $"{titleContains}:{captureRectangle.X},{captureRectangle.Y},{captureRectangle.Width},{captureRectangle.Height}");
    }

    internal static Rectangle CalculateCaptureRectangle(RoiSettings roi, int windowWidth, int windowHeight)
    {
        if (!roi.IsConfigured)
        {
            throw new InvalidOperationException("ROI is not configured. Use Select ROI before starting monitoring.");
        }

        if (windowWidth <= 0 || windowHeight <= 0)
        {
            throw new InvalidOperationException("Target window has no capturable area.");
        }

        if (roi.X < 0 ||
            roi.Y < 0 ||
            roi.X + roi.Width > windowWidth ||
            roi.Y + roi.Height > windowHeight)
        {
            throw new InvalidOperationException("ROI no longer fits inside the current HikmicroAnalyzer window. Select ROI again.");
        }

        return new Rectangle(roi.X, roi.Y, roi.Width, roi.Height);
    }

    private static Bitmap? CaptureWindowRoi(IntPtr handle, Rectangle roi, int windowWidth, int windowHeight)
    {
        using var windowBitmap = new Bitmap(windowWidth, windowHeight, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(windowBitmap);
        var hdc = graphics.GetHdc();
        try
        {
            if (!PrintWindow(handle, hdc, PrintWindowRenderFullContent) &&
                !PrintWindow(handle, hdc, 0))
            {
                return null;
            }
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }

        var roiBitmap = windowBitmap.Clone(roi, PixelFormat.Format24bppRgb);
        if (!LooksBlank(roiBitmap))
        {
            return roiBitmap;
        }

        roiBitmap.Dispose();
        return null;
    }

    private static Bitmap CaptureScreenRoi(WindowBounds bounds, Rectangle roi)
    {
        var bitmap = new Bitmap(roi.Width, roi.Height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Left + roi.X, bounds.Top + roi.Y, 0, 0, new Size(roi.Width, roi.Height), CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    private static Bitmap? CaptureForegroundScreenRoi(IntPtr handle, WindowBounds bounds, Rectangle roi)
    {
        if (GetForegroundWindow() != handle)
        {
            return null;
        }

        var bitmap = CaptureScreenRoi(bounds, roi);
        if (!LooksBlank(bitmap))
        {
            return bitmap;
        }

        bitmap.Dispose();
        return null;
    }

    private static Bitmap CaptureScreenRoiAfterForegroundRestore(IntPtr handle, WindowBounds bounds, Rectangle roi)
    {
        RestoreIfMinimized(handle);
        if (!SetForegroundWindow(handle))
        {
            throw new InvalidOperationException("Could not bring HikmicroAnalyzer to the foreground for fallback screen capture.");
        }

        Thread.Sleep(100);
        return CaptureScreenRoi(bounds, roi);
    }

    private static bool LooksBlank(Bitmap bitmap)
    {
        int? firstPixel = null;
        var stepX = Math.Max(1, bitmap.Width / 16);
        var stepY = Math.Max(1, bitmap.Height / 16);
        for (var y = 0; y < bitmap.Height; y += stepY)
        {
            for (var x = 0; x < bitmap.Width; x += stepX)
            {
                var pixel = bitmap.GetPixel(x, y).ToArgb();
                firstPixel ??= pixel;
                if (pixel != firstPixel.Value)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void RestoreIfMinimized(IntPtr handle)
    {
        if (IsIconic(handle))
        {
            _ = ShowWindow(handle, ShowWindowRestore);
        }
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

    private const uint PrintWindowRenderFullContent = 0x00000002;
    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
