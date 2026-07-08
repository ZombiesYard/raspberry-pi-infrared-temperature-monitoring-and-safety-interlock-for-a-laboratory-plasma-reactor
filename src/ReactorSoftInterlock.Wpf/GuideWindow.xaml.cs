using System.Windows;

namespace ReactorSoftInterlock.Wpf;

public partial class GuideWindow : Window
{
    public GuideWindow(string title, string body, string closeText, string guideTooltip, string closeTooltip)
    {
        InitializeComponent();
        Title = title;
        GuideTextBox.Text = body;
        GuideTextBox.ToolTip = guideTooltip;
        CloseButtonControl.Content = closeText;
        CloseButtonControl.ToolTip = closeTooltip;
        GuideTextBox.CaretIndex = 0;
        GuideTextBox.ScrollToHome();
    }
}
