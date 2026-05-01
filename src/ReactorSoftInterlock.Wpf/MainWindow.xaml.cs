using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
using ChartLine = System.Windows.Shapes.Line;
using ChartPolyline = System.Windows.Shapes.Polyline;

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
    private MonitorStatus _currentStatus = MonitorStatus.Idle;
    private bool _isBindingSettings;

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
        LanguageBox.ItemsSource = new[]
        {
            new LanguageOption("en", "English"),
            new LanguageOption("zh-CN", "中文"),
            new LanguageOption("de", "Deutsch")
        };
        BindSettingsToUi();
        ApplyLanguage();
        BuildServices();
        await LoadRecentHistoryAsync();
        DrawTemperatureChart();
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
            FooterText.Text = $"{T("footer.roiSaved")}: {_settings.Roi}";
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
            if (!ValidateMonitoringSetup())
            {
                return;
            }

            BuildServices();
            _monitoringCts = new CancellationTokenSource();
            SetStatus(MonitorStatus.Monitoring);
            FooterText.Text = T("footer.monitoringStarted");
            _ = Task.Run(() => RunMonitoringLoopAsync(_monitoringCts.Token));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopMonitoring(T("footer.monitoringStopped"));
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_stateMachine.CanReset(_lastTemperatureC))
            {
                MessageBox.Show(this, T("message.resetBlocked"), T("message.resetBlockedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var relayAction = await CreateRelay().ResetAsync(CancellationToken.None);
            _stateMachine.Reset(_lastTemperatureC);
            SetStatus(MonitorStatus.Monitoring);
            AlarmReasonText.Text = string.Empty;
            FooterText.Text = $"{T("footer.resetComplete")}: {relayAction}.";
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
            if (!ValidateRelaySetup(requireResetHex: false))
            {
                return;
            }

            var action = await CreateRelay().TestStopAsync(CancellationToken.None);
            FooterText.Text = $"{T("footer.relayTestComplete")}: {action}.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void TestRelayResetButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsFromUiAsync();
            if (!ValidateRelaySetup(requireResetHex: true))
            {
                return;
            }

            var action = await CreateRelay().ResetAsync(CancellationToken.None);
            FooterText.Text = $"{T("footer.relayResetComplete")}: {action}.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void OpenGuideButton_Click(object sender, RoutedEventArgs e)
    {
        var guide = new GuideWindow(T("guide.title"), T("guide.body"))
        {
            Owner = this
        };
        guide.ShowDialog();
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
            FooterText.Text = $"{T("footer.csvExported")}: {dialog.FileName}";
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
            FooterText.Text = $"{T("footer.settingsSaved")}: {_settingsPath}";
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
        var autoReset = new AutoResetOptions(_settings.AutoResetEnabled, _settings.RecoveryThresholdC, _settings.RecoveryStableSeconds);
        _monitoringService = new MonitoringService(reader, CreateRelay(), _sampleLog, new SystemClock(), _stateMachine, autoReset);
        _monitoringService.SampleRecorded += MonitoringService_SampleRecorded;
    }

    private IRelayController CreateRelay()
    {
        return _settings.Relay.DryRun
            ? new DryRunRelayController()
            : new SerialRelayController(_settings.Relay);
    }

    private async Task RunMonitoringLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _monitoringService!.RunAsync(TimeSpan.FromMilliseconds(_settings.PollIntervalMs), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Normal stop path.
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                StopMonitoring(T("footer.monitoringStopped"));
                ShowError(ex);
            });
        }
    }

    private bool ValidateMonitoringSetup()
    {
        if (!_settings.Roi.IsConfigured)
        {
            ShowSetupWarning(T("message.roiMissing"));
            return false;
        }

        if (!RuntimePathResolver.ExecutableExists(_settings.Ocr.TesseractExePath))
        {
            ShowSetupWarning(string.Format(CultureInfo.InvariantCulture, T("message.tesseractMissing"), _settings.Ocr.TesseractExePath));
            return false;
        }

        try
        {
            _ = new WindowCapture().GetWindowBounds(_settings.WindowTitleContains);
        }
        catch
        {
            ShowSetupWarning(string.Format(CultureInfo.InvariantCulture, T("message.hikmicroMissing"), _settings.WindowTitleContains));
            return false;
        }

        return ValidateRelaySetup(requireResetHex: true);
    }

    private bool ValidateRelaySetup(bool requireResetHex)
    {
        if (_settings.Relay.DryRun)
        {
            return true;
        }

        var availablePorts = SerialPort.GetPortNames();
        if (!availablePorts.Contains(_settings.Relay.PortName, StringComparer.OrdinalIgnoreCase))
        {
            ShowSetupWarning(string.Format(CultureInfo.InvariantCulture, T("message.comMissing"), _settings.Relay.PortName));
            return false;
        }

        if (!ValidateRelayCommand(_settings.Relay.StopCommandHex, T("label.stopHex")))
        {
            return false;
        }

        return !requireResetHex || ValidateRelayCommand(_settings.Relay.ResetCommandHex, T("label.resetHex"));
    }

    private bool ValidateRelayCommand(string commandText, string label)
    {
        var commands = RelayCommandTextParser.Parse(commandText);
        if (commands.Count == 0)
        {
            ShowSetupWarning(string.Format(CultureInfo.InvariantCulture, T("message.hexMissing"), label));
            return false;
        }

        if (commands.Any(static command => !command.StartsWith("AT", StringComparison.OrdinalIgnoreCase)))
        {
            ShowSetupWarning(string.Format(CultureInfo.InvariantCulture, T("message.hexInvalid"), label, commandText));
            return false;
        }

        return true;
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
            SetStatus(sample.Status);
            AlarmReasonText.Text = sample.AlarmReason;
            FooterText.Text = sample.RelayAction == RelayAction.StopSent
                ? T("footer.tripped")
                : $"{T("footer.lastSample")}: {sample.Timestamp:HH:mm:ss}";

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

            DrawTemperatureChart();
        });
    }

    private async Task LoadRecentHistoryAsync()
    {
        foreach (var sample in await _sampleLog.ReadRecentAsync(200, CancellationToken.None))
        {
            _history.Insert(0, sample);
        }

        DrawTemperatureChart();
    }

    private void StopMonitoring(string message)
    {
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
        _monitoringCts = null;
        SetStatus(_stateMachine.IsTripped ? MonitorStatus.Tripped : MonitorStatus.Idle);
        FooterText.Text = message;
    }

    private void BindSettingsToUi()
    {
        _isBindingSettings = true;
        LanguageBox.SelectedValue = NormalizeLanguage(_settings.Language);
        WindowTitleBox.Text = _settings.WindowTitleContains;
        ThresholdBox.Text = _settings.ThresholdC.ToString("0.0", CultureInfo.InvariantCulture);
        RecoveryThresholdBox.Text = _settings.RecoveryThresholdC.ToString("0.0", CultureInfo.InvariantCulture);
        StableSecondsBox.Text = _settings.RecoveryStableSeconds.ToString(CultureInfo.InvariantCulture);
        TesseractPathBox.Text = _settings.Ocr.TesseractExePath;
        IntervalBox.Text = _settings.PollIntervalMs.ToString(CultureInfo.InvariantCulture);
        PortBox.Text = _settings.Relay.PortName;
        BaudBox.Text = _settings.Relay.BaudRate.ToString(CultureInfo.InvariantCulture);
        StopHexBox.Text = _settings.Relay.StopCommandHex;
        ResetHexBox.Text = _settings.Relay.ResetCommandHex;
        DryRunBox.IsChecked = _settings.Relay.DryRun;
        AutoResetBox.IsChecked = _settings.AutoResetEnabled;
        _isBindingSettings = false;
    }

    private async Task SaveSettingsFromUiAsync()
    {
        _settings.Language = NormalizeLanguage(LanguageBox.SelectedValue?.ToString() ?? _settings.Language);
        _settings.WindowTitleContains = WindowTitleBox.Text.Trim();
        _settings.ThresholdC = ParseDouble(ThresholdBox.Text, nameof(_settings.ThresholdC));
        _settings.RecoveryThresholdC = ParseDouble(RecoveryThresholdBox.Text, nameof(_settings.RecoveryThresholdC));
        _settings.RecoveryStableSeconds = ParseInt(StableSecondsBox.Text, nameof(_settings.RecoveryStableSeconds));
        _settings.AutoResetEnabled = AutoResetBox.IsChecked == true;
        _settings.PollIntervalMs = ParseInt(IntervalBox.Text, nameof(_settings.PollIntervalMs));
        _settings.Ocr.TesseractExePath = TesseractPathBox.Text.Trim();
        _settings.Relay.PortName = PortBox.Text.Trim();
        _settings.Relay.BaudRate = ParseInt(BaudBox.Text, nameof(_settings.Relay.BaudRate));
        _settings.Relay.StopCommandHex = StopHexBox.Text.Trim();
        _settings.Relay.ResetCommandHex = ResetHexBox.Text.Trim();
        _settings.Relay.DryRun = DryRunBox.IsChecked == true;
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
    }

    private async void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isBindingSettings || _settings is null)
        {
            return;
        }

        _settings.Language = NormalizeLanguage(LanguageBox.SelectedValue?.ToString() ?? "en");
        ApplyLanguage();
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
    }

    private void TemperatureChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawTemperatureChart();
    }

    private void ApplyLanguage()
    {
        Title = T("app.title");
        TitleText.Text = T("app.title");
        SubtitleText.Text = T("app.subtitle");
        StatusLabel.Text = T("label.status");
        TemperatureGroup.Header = T("group.temperature");
        ControlsGroup.Header = T("group.controls");
        ConfigurationGroup.Header = T("group.configuration");
        ChartGroup.Header = T("group.chart");
        SelectRoiButton.Content = T("button.selectRoi");
        StartButton.Content = T("button.start");
        StopButton.Content = T("button.stop");
        ResetButton.Content = T("button.reset");
        TestRelayButton.Content = T("button.testRelay");
        TestRelayResetButton.Content = T("button.testRelayReset");
        ExportButton.Content = T("button.export");
        OpenGuideButton.Content = T("button.guide");
        SaveSettingsButton.Content = T("button.save");
        WindowLabel.Text = T("label.window");
        ThresholdLabel.Text = T("label.threshold");
        TesseractLabel.Text = T("label.tesseract");
        IntervalLabel.Text = T("label.interval");
        PortLabel.Text = T("label.com");
        BaudLabel.Text = T("label.baud");
        StopHexLabel.Text = T("label.stopHex");
        ResetHexLabel.Text = T("label.resetHex");
        LanguageLabel.Text = T("label.language");
        RecoveryLabel.Text = T("label.recovery");
        StableSecondsLabel.Text = T("label.stable");
        DryRunBox.Content = T("check.dryRun");
        AutoResetBox.Content = T("check.autoReset");
        TimeColumn.Header = T("grid.time");
        TemperatureColumn.Header = T("grid.temp");
        StatusColumn.Header = T("grid.status");
        RelayColumn.Header = T("grid.relay");
        ReasonColumn.Header = T("grid.reason");
        RoiColumn.Header = T("grid.roi");
        SetStatus(_currentStatus);
        if (FooterText.Text == "Ready." || FooterText.Text == UiText.Get("en", "footer.ready"))
        {
            FooterText.Text = T("footer.ready");
        }

        DrawTemperatureChart();
    }

    private void DrawTemperatureChart()
    {
        if (_settings is null || TemperatureChartCanvas.ActualWidth <= 10 || TemperatureChartCanvas.ActualHeight <= 10)
        {
            return;
        }

        TemperatureChartCanvas.Children.Clear();
        var width = TemperatureChartCanvas.ActualWidth;
        var height = TemperatureChartCanvas.ActualHeight;
        const double left = 44;
        const double right = 12;
        const double top = 14;
        const double bottom = 28;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);
        var samples = _history.Reverse().TakeLast(500).ToList();
        var valid = samples.Where(static sample => sample.TemperatureC is not null).ToList();

        DrawLine(left, top, left, top + plotHeight, Brushes.LightGray, 1);
        DrawLine(left, top + plotHeight, left + plotWidth, top + plotHeight, Brushes.LightGray, 1);

        if (valid.Count == 0)
        {
            AddChartText(T("chart.noData"), left + 8, top + 12, Brushes.Gray);
            return;
        }

        var minTemp = Math.Min(valid.Min(static sample => sample.TemperatureC!.Value), _settings.RecoveryThresholdC) - 3;
        var maxTemp = Math.Max(valid.Max(static sample => sample.TemperatureC!.Value), _settings.ThresholdC) + 3;
        if (Math.Abs(maxTemp - minTemp) < 1)
        {
            maxTemp += 1;
            minTemp -= 1;
        }

        double X(int index) => left + (samples.Count <= 1 ? 0 : index * plotWidth / (samples.Count - 1));
        double Y(double temp) => top + (maxTemp - temp) * plotHeight / (maxTemp - minTemp);

        DrawReferenceLine(_settings.ThresholdC, Brushes.DarkRed, $"{T("chart.threshold")} {_settings.ThresholdC:0.0} C", left, plotWidth, Y);
        DrawReferenceLine(_settings.RecoveryThresholdC, Brushes.SeaGreen, $"{T("chart.recovery")} {_settings.RecoveryThresholdC:0.0} C", left, plotWidth, Y);

        var currentPoints = new PointCollection();
        for (var i = 0; i < samples.Count; i++)
        {
            var temp = samples[i].TemperatureC;
            if (temp is null)
            {
                AddPolyline(currentPoints);
                currentPoints = new PointCollection();
                continue;
            }

            currentPoints.Add(new Point(X(i), Y(temp.Value)));
        }

        AddPolyline(currentPoints);
        AddChartText($"{maxTemp:0} C", 4, top - 4, Brushes.Gray);
        AddChartText($"{minTemp:0} C", 4, top + plotHeight - 10, Brushes.Gray);
    }

    private void DrawReferenceLine(double temp, Brush brush, string label, double left, double plotWidth, Func<double, double> yFactory)
    {
        var y = yFactory(temp);
        DrawLine(left, y, left + plotWidth, y, brush, 1);
        AddChartText(label, left + 6, y - 18, brush);
    }

    private void DrawLine(double x1, double y1, double x2, double y2, Brush brush, double thickness)
    {
        TemperatureChartCanvas.Children.Add(new ChartLine
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = thickness
        });
    }

    private void AddPolyline(PointCollection points)
    {
        if (points.Count < 2)
        {
            return;
        }

        TemperatureChartCanvas.Children.Add(new ChartPolyline
        {
            Points = points,
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 2
        });
    }

    private void AddChartText(string text, double x, double y, Brush brush)
    {
        var block = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = 11
        };
        Canvas.SetLeft(block, x);
        Canvas.SetTop(block, y);
        TemperatureChartCanvas.Children.Add(block);
    }

    private void SetStatus(MonitorStatus status)
    {
        _currentStatus = status;
        StatusText.Text = T($"status.{status}");
    }

    private string T(string key)
    {
        return UiText.Get(NormalizeLanguage(_settings?.Language ?? "en"), key);
    }

    private static string NormalizeLanguage(string language)
    {
        return language is "zh-CN" or "de" ? language : "en";
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

    private void ShowSetupWarning(string message)
    {
        FooterText.Text = message;
        MessageBox.Show(this, message, T("message.setupRequiredTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
