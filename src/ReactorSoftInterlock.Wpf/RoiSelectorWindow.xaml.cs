using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ReactorSoftInterlock.Infrastructure.Capture;

namespace ReactorSoftInterlock.Wpf;

public partial class RoiSelectorWindow : Window
{
    private readonly WindowBounds _targetBounds;
    private Point? _dragStart;

    public RoiSelectorWindow(WindowBounds targetBounds)
    {
        _targetBounds = targetBounds;
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        Loaded += RoiSelectorWindow_Loaded;
        MouseDown += RoiSelectorWindow_MouseDown;
        MouseMove += RoiSelectorWindow_MouseMove;
        MouseUp += RoiSelectorWindow_MouseUp;
        KeyDown += RoiSelectorWindow_KeyDown;
    }

    public Rect? SelectedRect { get; private set; }

    private void RoiSelectorWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var topLeft = PointFromScreen(new Point(_targetBounds.Left, _targetBounds.Top));
        var bottomRight = PointFromScreen(new Point(_targetBounds.Left + _targetBounds.Width, _targetBounds.Top + _targetBounds.Height));
        Canvas.SetLeft(TargetWindowRectangle, topLeft.X);
        Canvas.SetTop(TargetWindowRectangle, topLeft.Y);
        TargetWindowRectangle.Width = Math.Max(1, bottomRight.X - topLeft.X);
        TargetWindowRectangle.Height = Math.Max(1, bottomRight.Y - topLeft.Y);
    }

    private void RoiSelectorWindow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        SelectionRectangle.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    private void RoiSelectorWindow_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is null)
        {
            return;
        }

        DrawSelection(_dragStart.Value, e.GetPosition(this));
    }

    private void RoiSelectorWindow_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null)
        {
            return;
        }

        ReleaseMouseCapture();
        var end = e.GetPosition(this);
        DrawSelection(_dragStart.Value, end);
        var screenStart = PointToScreen(_dragStart.Value);
        var screenEnd = PointToScreen(end);
        var left = Math.Min(screenStart.X, screenEnd.X);
        var top = Math.Min(screenStart.Y, screenEnd.Y);
        var width = Math.Abs(screenEnd.X - screenStart.X);
        var height = Math.Abs(screenEnd.Y - screenStart.Y);

        if (width >= 5 && height >= 5)
        {
            SelectedRect = new Rect(left, top, width, height);
            DialogResult = true;
        }
        else
        {
            DialogResult = false;
        }
    }

    private void RoiSelectorWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    private void DrawSelection(Point start, Point end)
    {
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = Math.Abs(end.X - start.X);
        SelectionRectangle.Height = Math.Abs(end.Y - start.Y);
    }
}
