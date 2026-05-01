using System.Windows;

namespace ReactorSoftInterlock.Wpf;

public partial class GuideWindow : Window
{
    public GuideWindow(string title, string body)
    {
        InitializeComponent();
        Title = title;
        GuideTextBox.Text = body;
        GuideTextBox.CaretIndex = 0;
        GuideTextBox.ScrollToHome();
    }
}
