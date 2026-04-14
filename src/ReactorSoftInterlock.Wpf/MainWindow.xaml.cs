using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ReactorSoftInterlock.Application;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;
using ReactorSoftInterlock.Infrastructure;
using ReactorSoftInterlock.Infrastructure.Capture;
using ReactorSoftInterlock.Infrastructure.Logging;
using ReactorSoftInterlock.Infrastructure.Ocr;
using ReactorSoftInterlock.Infrastructure.Relay;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Wpf;

public partial class MainWindow : Window
{
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    private readonly ObservableCollection<TemperatureSample> _history = [];
    private SettingsStore _settingsStore = null!;
    private AppSettings _settings = null!;
    private CsvSampleLog _sampleLog = null!;
    private InterlockStateMachine _stateMachine = null!;
    private MonitoringService? _monitoringService;
    private CancellationTokenSource? _monitoringCts;
    private double? _lastTemperatureC;

    public MainWindow()
    {
        InitializeComponent();
        HistoryGrid.ItemsSource = _history;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settingsStore = new SettingsStore(_settingsPath);
        _settings = await _settingsStore.LoadAsync(CancellationToken.None);
        BindSettingsToUi();
        BuildServices();
        await LoadRecentHistoryAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
    }

    private async void SelectRoiButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsFromUiAsync();
            var capture = new WindowCapture();
            var windowBounds = capture.GetWindowBounds(_settings.WindowTitleContains);
            var selector = new RoiSelectorWindow(windowBounds) { Owner = this };
            if (selector.ShowDialog() != true || selector.SelectedRect is null)
            {
                return;
            }

            var selected = selector.SelectedRect.Value;
            _settings.Roi.X = (int)Math.Max(0, selected.X - windowBounds.Left);
            _settings.Roi.Y = (int)Math.Max(0, selected.Y - windowBounds.Top);
            _settings.Roi.Width = (int)Math.Max(1, selected.Width);
            _settings.Roi.Height = (int)Math.Max(1, selected.Height);
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            FooterText.Text = $"ROI saved: {_settings.Roi}";
            BuildServices();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_monitoringCts is not null)
            {
                return;
            }

            await SaveSettingsFromUiAsync();
            BuildServices();
            _monitoringCts = new CancellationTokenSource();
            StatusText.Text = "Monitoring";
            FooterText.Text = "Monitoring started.";
            _ = Task.Run(() => _monitoringService!.RunAsync(TimeSpan.FromMilliseconds(_settings.PollIntervalMs), _monitoringCts.Token));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopMonitoring("Monitoring stopped.");
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_stateMachine.CanReset(_lastTemperatureC))
            {
                MessageBox.Show(this, "Reset is allowed only after a valid temperature below the threshold is read.", "Reset blocked", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var relayAction = await CreateRelay().ResetAsync(CancellationToken.None);
            _stateMachine.Reset(_lastTemperatureC);
            StatusText.Text = "Monitoring";
            AlarmReasonText.Text = string.Empty;
            FooterText.Text = $"Reset complete: {relayAction}.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void TestRelayButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsFromUiAsync();
            var action = await CreateRelay().TestStopAsync(CancellationToken.None);
            FooterText.Text = $"Relay test completed: {action}.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                FileName = $"g2000-temperature-history-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            await _sampleLog.ExportAsync(dialog.FileName, CancellationToken.None);
            FooterText.Text = $"CSV exported: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsFromUiAsync();
            BuildServices();
            FooterText.Text = $"Settings saved: {_settingsPath}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void BuildServices()
    {
        var dataDirectory = Path.IsPathRooted(_settings.DataDirectory)
            ? _settings.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, _settings.DataDirectory);

        _sampleLog = new CsvSampleLog(Path.Combine(dataDirectory, "temperature-history.csv"));
        _stateMachine = new InterlockStateMachine(new InterlockSettings(_settings.ThresholdC));
        var reader = new TesseractCliTemperatureReader(new WindowCapture(), new TemperatureTextParser(), _settings);
        _monitoringService = new MonitoringService(reader, CreateRelay(), _sampleLog, new SystemClock(), _stateMachine);
        _monitoringService.SampleRecorded += MonitoringService_SampleRecorded;
    }

    private IRelayController CreateRelay()
    {
        return _settings.Relay.DryRun
            ? new DryRunRelayController()
            : new SerialRelayController(_settings.Relay);
    }

    private void MonitoringService_SampleRecorded(object? sender, TemperatureSample sample)
    {
        Dispatcher.Invoke(() =>
        {
            _lastTemperatureC = sample.TemperatureC;
            _history.Insert(0, sample);
            while (_history.Count > 500)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            TemperatureText.Text = sample.TemperatureC is null
                ? "NO READING"
                : $"{sample.TemperatureC.Value:0.0} C";
            RawOcrText.Text = string.IsNullOrWhiteSpace(sample.RawOcrText) ? "(empty OCR text)" : sample.RawOcrText;
            StatusText.Text = sample.Status.ToString();
            AlarmReasonText.Text = sample.AlarmReason;
            FooterText.Text = sample.RelayAction == RelayAction.StopSent
                ? "TRIPPED: relay stop command sent."
                : $"Last sample: {sample.Timestamp:HH:mm:ss}";

            if (sample.Status == MonitorStatus.Tripped)
            {
                StatusText.Foreground = System.Windows.Media.Brushes.DarkRed;
                TemperatureText.Foreground = System.Windows.Media.Brushes.DarkRed;
            }
            else
            {
                StatusText.Foreground = System.Windows.Media.Brushes.Black;
                TemperatureText.Foreground = System.Windows.Media.Brushes.Black;
            }
        });
    }

    private async Task LoadRecentHistoryAsync()
    {
        foreach (var sample in await _sampleLog.ReadRecentAsync(200, CancellationToken.None))
        {
            _history.Insert(0, sample);
        }
    }

    private void StopMonitoring(string message)
    {
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
        _monitoringCts = null;
        StatusText.Text = _stateMachine.IsTripped ? "Tripped" : "Idle";
        FooterText.Text = message;
    }

    private void BindSettingsToUi()
    {
        WindowTitleBox.Text = _settings.WindowTitleContains;
        ThresholdBox.Text = _settings.ThresholdC.ToString("0.0", CultureInfo.InvariantCulture);
        TesseractPathBox.Text = _settings.Ocr.TesseractExePath;
        IntervalBox.Text = _settings.PollIntervalMs.ToString(CultureInfo.InvariantCulture);
        PortBox.Text = _settings.Relay.PortName;
        BaudBox.Text = _settings.Relay.BaudRate.ToString(CultureInfo.InvariantCulture);
        StopHexBox.Text = _settings.Relay.StopCommandHex;
        ResetHexBox.Text = _settings.Relay.ResetCommandHex;
        DryRunBox.IsChecked = _settings.Relay.DryRun;
    }

    private async Task SaveSettingsFromUiAsync()
    {
        _settings.WindowTitleContains = WindowTitleBox.Text.Trim();
        _settings.ThresholdC = ParseDouble(ThresholdBox.Text, nameof(_settings.ThresholdC));
        _settings.PollIntervalMs = ParseInt(IntervalBox.Text, nameof(_settings.PollIntervalMs));
        _settings.Ocr.TesseractExePath = TesseractPathBox.Text.Trim();
        _settings.Relay.PortName = PortBox.Text.Trim();
        _settings.Relay.BaudRate = ParseInt(BaudBox.Text, nameof(_settings.Relay.BaudRate));
        _settings.Relay.StopCommandHex = StopHexBox.Text.Trim();
        _settings.Relay.ResetCommandHex = ResetHexBox.Text.Trim();
        _settings.Relay.DryRun = DryRunBox.IsChecked == true;
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
    }

    private static double ParseDouble(string text, string name)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"{name} must be a number. Use dot as decimal separator, for example 90.0.");
        }

        return value;
    }

    private static int ParseInt(string text, string name)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"{name} must be an integer.");
        }

        return value;
    }

    private void ShowError(Exception ex)
    {
        FooterText.Text = ex.Message;
        MessageBox.Show(this, ex.Message, "G2000 Soft Interlock", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
