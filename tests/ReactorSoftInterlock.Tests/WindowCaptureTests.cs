using ReactorSoftInterlock.Infrastructure.Capture;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Tests;

public sealed class WindowCaptureTests
{
    [Fact]
    public void CalculateCaptureRectangle_KeepsValidRoi()
    {
        var roi = new RoiSettings { X = 10, Y = 20, Width = 30, Height = 40 };

        var rectangle = WindowCapture.CalculateCaptureRectangle(roi, windowWidth: 200, windowHeight: 100);

        Assert.Equal(10, rectangle.X);
        Assert.Equal(20, rectangle.Y);
        Assert.Equal(30, rectangle.Width);
        Assert.Equal(40, rectangle.Height);
    }

    [Fact]
    public void CalculateCaptureRectangle_RejectsPartiallyOutsideRoi()
    {
        var roi = new RoiSettings { X = 90, Y = 45, Width = 30, Height = 20 };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WindowCapture.CalculateCaptureRectangle(roi, windowWidth: 100, windowHeight: 50));

        Assert.Contains("Select ROI again", ex.Message);
    }

    [Fact]
    public void CalculateCaptureRectangle_RejectsRoiOutsideCurrentWindow()
    {
        var roi = new RoiSettings { X = 120, Y = 10, Width = 30, Height = 20 };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WindowCapture.CalculateCaptureRectangle(roi, windowWidth: 100, windowHeight: 50));

        Assert.Contains("Select ROI again", ex.Message);
    }
}
