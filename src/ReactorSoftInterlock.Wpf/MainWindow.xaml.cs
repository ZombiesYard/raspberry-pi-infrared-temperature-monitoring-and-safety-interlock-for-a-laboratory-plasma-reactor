using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ReactorSoftInterlock.Application;
using ReactorSoftInterlock.Application.Ports;
using ReactorSoftInterlock.Domain;
using ReactorSoftInterlock.Infrastructure;
using ReactorSoftInterlock.Infrastructure.Capture;
using ReactorSoftInterlock.Infrastructure.Gas;
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
    private readonly Dictionary<int, bool?> _channelStates = new();
    private readonly List<ChannelUi> _channelUis = [];
    private SettingsStore _settingsStore = null!;
    private AppSettings _settings = null!;
    private CsvSampleLog _sampleLog = null!;
    private InterlockStateMachine _stateMachine = null!;
    private MonitoringService? _monitoringService;
    private CancellationTokenSource? _monitoringCts;
    private readonly DispatcherTimer _gasFlowTimer = new();
    private double? _lastTemperatureC;
    private double? _lastGasFlowMlMin;
    private MonitorStatus _currentStatus = MonitorStatus.Idle;
    private bool _isBindingSettings;
    private bool _isRefreshingGasFlow;

    public MainWindow()
    {
        InitializeComponent();
        HistoryGrid.ItemsSource = _history;
        InitializeChannelUi();
        _gasFlowTimer.Interval = TimeSpan.FromSeconds(1);
        _gasFlowTimer.Tick += GasFlowTimer_Tick;
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
        SetAllChannelStates(null);
        SetCurrentMode(T("mode.monitoring"));
        UpdateGasFlowDisplay();
        _gasFlowTimer.Start();
        await LoadRecentHistoryAsync();
        DrawTemperatureChart();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _gasFlowTimer.Stop();
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
    }

    private async void GasFlowTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshGasFlowAsync();
    }

    private void InitializeChannelUi()
    {
        _channelUis.Add(new ChannelUi(1, Channel1NameText, Channel1MappingText, Channel1StateText, Channel1OpenButton, Channel1CloseButton));
        _channelUis.Add(new ChannelUi(2, Channel2NameText, Channel2MappingText, Channel2StateText, Channel2OpenButton, Channel2CloseButton));
        _channelUis.Add(new ChannelUi(3, Channel3NameText, Channel3MappingText, Channel3StateText, Channel3OpenButton, Channel3CloseButton));
        _channelUis.Add(new ChannelUi(4, Channel4NameText, Channel4MappingText, Channel4StateText, Channel4OpenButton, Channel4CloseButton));
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
            SetCurrentMode(T("mode.monitoring"));
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

            var relayAction = await CreateProcessOutputController().ResetAsync(CancellationToken.None);
            _stateMachine.Reset(_lastTemperatureC);
            SetStatus(MonitorStatus.Monitoring);
            SetAllChannelStates(true);
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
            if (!ValidateRelaySetup(requireRestore: false))
            {
                return;
            }

            await CreateRelayBank().OpenAllInterlocksAsync(CancellationToken.None);
            SetAllChannelStates(false);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = T("footer.allDisconnected");
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
            if (!ValidateRelaySetup(requireRestore: true))
            {
                return;
            }

            await CreateRelayBank().CloseAllInterlocksAsync(CancellationToken.None);
            SetAllChannelStates(true);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = T("footer.allConnected");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void ChannelOpenButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteChannelActionAsync(sender, closed: false, T("footer.channelDisconnected"));
    }

    private async void ChannelCloseButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteChannelActionAsync(sender, closed: true, T("footer.channelConnected"));
    }

    private async Task ExecuteChannelActionAsync(object sender, bool closed, string footerTemplate)
    {
        try
        {
            await SaveSettingsFromUiAsync();
            if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var channelNumber))
            {
                return;
            }

            if (!ValidateRelaySetup(requireRestore: true, specificChannelNumber: channelNumber))
            {
                return;
            }

            await CreateRelayBank().SetChannelClosedAsync(channelNumber, closed, CancellationToken.None);
            SetChannelState(channelNumber, closed);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = string.Format(CultureInfo.InvariantCulture, footerTemplate, channelNumber);
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
            await RebuildOrRestartMonitoringAsync();
            FooterText.Text = $"{T("footer.settingsSaved")}: {_settingsPath}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void AdvancedSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsFromUiAsync();
            var window = new AdvancedSettingsWindow(_settings.Relay, NormalizeLanguage(_settings.Language))
            {
                Owner = this
            };

            if (window.ShowDialog() != true)
            {
                return;
            }

            _settings.Relay = window.ResultSettings;
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            await RebuildOrRestartMonitoringAsync();
            BindSettingsToUi();
            ApplyLanguage();
            FooterText.Text = T("footer.advancedSaved");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, T("about.body"), T("about.title"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void RestoreMonitoringControlButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsFromUiAsync();
            if (!ValidateRelaySetup(requireRestore: true))
            {
                return;
            }

            await CreateRelayBank().CloseAllInterlocksAsync(CancellationToken.None);
            SetAllChannelStates(true);
            SetCurrentMode(T("mode.monitoring"));
            MainTabControl.SelectedItem = MonitorTab;
            FooterText.Text = T("footer.monitoringControlRestored");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void StopGasButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsFromUiAsync();
            if (!ValidateAmc2100Setup())
            {
                return;
            }

            await CreateGasFlowController().StopFlowAsync(CancellationToken.None);
            await RefreshGasFlowAsync(force: true);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = T("footer.gasStopped");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void RestoreGasButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsFromUiAsync();
            if (!ValidateAmc2100Setup())
            {
                return;
            }

            await CreateGasFlowController().SetTargetFlowAsync(_settings.Amc2100.FallbackRestoreSetpointMlMin, CancellationToken.None);
            await RefreshGasFlowAsync(force: true);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = string.Format(
                CultureInfo.InvariantCulture,
                T("footer.gasSetpointApplied"),
                _settings.Amc2100.FallbackRestoreSetpointMlMin.ToString("0.0", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void BuildServices()
    {
        _settings.Relay.Normalize();
        _settings.Amc2100.Normalize();
        var dataDirectory = Path.IsPathRooted(_settings.DataDirectory)
            ? _settings.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, _settings.DataDirectory);

        _sampleLog = new CsvSampleLog(Path.Combine(dataDirectory, "temperature-history.csv"));
        _stateMachine = new InterlockStateMachine(new InterlockSettings(_settings.ThresholdC));
        var reader = new TesseractCliTemperatureReader(new WindowCapture(), new TemperatureTextParser(), _settings);
        var autoReset = new AutoResetOptions(_settings.AutoResetEnabled, _settings.RecoveryThresholdC, _settings.RecoveryStableSeconds);
        _monitoringService = new MonitoringService(reader, CreateProcessOutputController(), _sampleLog, new SystemClock(), _stateMachine, autoReset);
        _monitoringService.SampleRecorded += MonitoringService_SampleRecorded;
        UpdateGasFlowDisplay();
    }

    private IRelayBankController CreateRelayBank()
    {
        return _settings.Relay.DryRun
            ? new DryRunRelayController()
            : new SerialRelayController(_settings.Relay);
    }

    private IGasFlowController CreateGasFlowController()
    {
        return !_settings.Amc2100.Enabled
            ? new NoOpGasFlowController()
            : new Amc2100GasFlowController(_settings.Amc2100);
    }

    private IRelayBankController CreateProcessOutputController()
    {
        return new ProcessOutputController(CreateRelayBank());
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

        return ValidateRelaySetup(requireRestore: true);
    }

    private bool ValidateAmc2100Setup()
    {
        _settings.Amc2100.Normalize();

        if (!_settings.Amc2100.Enabled)
        {
            return true;
        }

        var availablePorts = SerialPort.GetPortNames();
        if (!availablePorts.Contains(_settings.Amc2100.PortName, StringComparer.OrdinalIgnoreCase))
        {
            ShowSetupWarning(string.Format(CultureInfo.InvariantCulture, T("message.amcComMissing"), _settings.Amc2100.PortName));
            return false;
        }

        if (_settings.Amc2100.SlaveAddress is < 1 or > 247)
        {
            ShowSetupWarning(T("message.amcSlaveInvalid"));
            return false;
        }

        return true;
    }

    private bool ValidateRelaySetup(bool requireRestore, int? specificChannelNumber = null)
    {
        _settings.Relay.Normalize();

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

        var channels = _settings.Relay.Channels
            .Where(channel => channel.Enabled && (specificChannelNumber is null || channel.ChannelNumber == specificChannelNumber.Value))
            .OrderBy(channel => channel.ChannelNumber)
            .ToList();

        if (channels.Count == 0)
        {
            ShowSetupWarning(T("message.noRelayChannels"));
            return false;
        }

        foreach (var channel in channels)
        {
            if (!ValidateRelayCommand(channel.OpenCommand, $"{channel.DisplayName} {T("label.disconnect")}"))
            {
                return false;
            }

            if (requireRestore && !ValidateRelayCommand(channel.CloseCommand, $"{channel.DisplayName} {T("label.connect")}"))
            {
                return false;
            }
        }

        return true;
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

            if (sample.RelayAction == RelayAction.StopSent)
            {
                SetAllChannelStates(false);
                SetCurrentMode(T("mode.monitoring"));
            }
            else if (sample.RelayAction == RelayAction.ResetSent)
            {
                SetAllChannelStates(true);
                SetCurrentMode(T("mode.monitoring"));
            }

            FooterText.Text = sample.RelayAction == RelayAction.StopSent
                ? T("footer.tripped")
                : $"{T("footer.lastSample")}: {sample.Timestamp:HH:mm:ss}";

            DrawTemperatureChart();
        });
    }

    private async Task LoadRecentHistoryAsync()
    {
        foreach (var sample in await _sampleLog.ReadRecentAsync(200, CancellationToken.None))
        {
            _history.Insert(0, sample);
        }

        var latest = _history.FirstOrDefault();
        if (latest is not null)
        {
            _lastTemperatureC = latest.TemperatureC;
            if (latest.RelayAction == RelayAction.StopSent || latest.Status == MonitorStatus.Tripped)
            {
                SetAllChannelStates(false);
            }
            else if (latest.RelayAction == RelayAction.ResetSent || latest.Status == MonitorStatus.Monitoring)
            {
                SetAllChannelStates(true);
            }
        }

        DrawTemperatureChart();
        await RefreshGasFlowAsync(force: true);
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
        DryRunBox.IsChecked = _settings.Relay.DryRun;
        AutoResetBox.IsChecked = _settings.AutoResetEnabled;
        AmcEnabledBox.IsChecked = _settings.Amc2100.Enabled;
        AmcPortBox.Text = _settings.Amc2100.PortName;
        AmcBaudBox.Text = _settings.Amc2100.BaudRate.ToString(CultureInfo.InvariantCulture);
        AmcSlaveBox.Text = _settings.Amc2100.SlaveAddress.ToString(CultureInfo.InvariantCulture);
        AmcFallbackBox.Text = _settings.Amc2100.FallbackRestoreSetpointMlMin.ToString("0.0", CultureInfo.InvariantCulture);
        AmcForceDigitalModeBox.IsChecked = _settings.Amc2100.ForceDigitalControlMode;
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
        _settings.Relay.DryRun = DryRunBox.IsChecked == true;
        _settings.Amc2100.Enabled = AmcEnabledBox.IsChecked == true;
        _settings.Amc2100.PortName = AmcPortBox.Text.Trim();
        _settings.Amc2100.BaudRate = ParseInt(AmcBaudBox.Text, nameof(_settings.Amc2100.BaudRate));
        _settings.Amc2100.SlaveAddress = ParseInt(AmcSlaveBox.Text, nameof(_settings.Amc2100.SlaveAddress));
        _settings.Amc2100.FallbackRestoreSetpointMlMin = ParseDouble(AmcFallbackBox.Text, nameof(_settings.Amc2100.FallbackRestoreSetpointMlMin));
        _settings.Amc2100.ForceDigitalControlMode = AmcForceDigitalModeBox.IsChecked == true;
        _settings.Relay.Normalize();
        _settings.Amc2100.Normalize();
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        UpdateGasFlowDisplay();
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
        FileMenu.Header = T("menu.file");
        ExportMenuItem.Header = T("menu.export");
        ExitMenuItem.Header = T("menu.exit");
        ToolsMenu.Header = T("menu.tools");
        SelectRoiMenuItem.Header = T("button.selectRoi");
        GuideMenuItem.Header = T("button.guide");
        AdvancedSettingsMenuItem.Header = T("menu.advanced");
        HelpMenu.Header = T("menu.help");
        WiringNotesMenuItem.Header = T("menu.wiring");
        AboutMenuItem.Header = T("menu.about");
        StatusLabel.Text = T("label.status");
        TemperatureCardLabel.Text = T("group.temperature");
        RelaySummaryLabel.Text = T("label.relayBank");
        GasFlowCardLabel.Text = T("label.gasFlow");
        GasFlowHintText.Text = T("gasFlow.hint");
        CurrentModeLabel.Text = T("label.currentMode");
        EngineeringHintText.Text = T("engineering.hint");
        MonitorTab.Header = T("tab.monitor");
        EngineeringTab.Header = T("tab.engineering");
        ControlsCardTitle.Text = T("group.controls");
        SettingsCardTitle.Text = T("group.configuration");
        ChartGroupTitle.Text = T("group.chart");
        HistoryCardTitle.Text = T("group.history");
        EngineeringCardTitle.Text = T("group.engineering");
        EngineeringActionsTitle.Text = T("group.groupedActions");
        EngineeringNotesTitle.Text = T("group.wiringNotes");
        EngineeringChecklistText.Text = T("engineering.checklist");
        EngineeringNotesText.Text = T("engineering.notes");
        StartButton.Content = T("button.start");
        StopButton.Content = T("button.stop");
        ResetButton.Content = T("button.reset");
        SelectRoiButton.Content = T("button.selectRoi");
        ExportButton.Content = T("button.export");
        TestRelayButton.Content = T("button.disconnectAll");
        TestRelayResetButton.Content = T("button.connectAll");
        TripAllEngineeringButton.Content = T("button.disconnectAll");
        RestoreAllEngineeringButton.Content = T("button.connectAll");
        RestoreMonitoringControlButton.Content = T("button.restoreMonitoring");
        SaveSettingsButton.Content = T("button.save");
        WindowLabel.Text = T("label.window");
        ThresholdLabel.Text = T("label.threshold");
        TesseractLabel.Text = T("label.tesseract");
        IntervalLabel.Text = T("label.interval");
        PortQuickLabel.Text = T("label.com");
        RecoveryLabel.Text = T("label.recovery");
        StableSecondsLabel.Text = T("label.stable");
        LanguageQuickLabel.Text = T("label.language");
        DryRunBox.Content = T("check.dryRun");
        AutoResetBox.Content = T("check.autoReset");
        AmcSectionTitle.Text = T("group.amc2100");
        AmcEnabledBox.Content = T("check.amcEnabled");
        AmcPortLabel.Text = T("label.amcCom");
        AmcBaudLabel.Text = T("label.amcBaud");
        AmcSlaveLabel.Text = T("label.amcSlave");
        AmcFallbackLabel.Text = T("label.amcRestoreSetpoint");
        AmcForceDigitalModeBox.Content = T("check.amcDigitalMode");
        TimeColumn.Header = T("grid.time");
        TemperatureColumn.Header = T("grid.temp");
        StatusColumn.Header = T("grid.status");
        RelayColumn.Header = T("grid.relay");
        ReasonColumn.Header = T("grid.reason");
        RoiColumn.Header = T("grid.roi");
        StopGasButton.Content = T("button.stopGas");
        RestoreGasButton.Content = T("button.restoreGas");
        ApplyChannelLabels();
        SetStatus(_currentStatus);

        if (string.IsNullOrWhiteSpace(FooterText.Text) || FooterText.Text == "Ready." || FooterText.Text == UiText.Get("en", "footer.ready"))
        {
            FooterText.Text = T("footer.ready");
        }

        DrawTemperatureChart();
        UpdateRelayBankStatus();
        UpdateGasFlowDisplay();
    }

    private void ApplyChannelLabels()
    {
        foreach (var channel in _channelUis)
        {
            var configured = _settings.Relay.Channels.FirstOrDefault(item => item.ChannelNumber == channel.ChannelNumber);
            channel.NameText.Text = configured?.DisplayName ?? $"CH{channel.ChannelNumber}";
            channel.MappingText.Text = T($"channel.{channel.ChannelNumber}.mapping");
            channel.OpenButton.Content = T("button.disconnect");
            channel.CloseButton.Content = T("button.connect");
        }
    }

    private void SetAllChannelStates(bool? closed)
    {
        foreach (var channel in _channelUis)
        {
            _channelStates[channel.ChannelNumber] = closed;
        }

        UpdateRelayBankStatus();
    }

    private void SetChannelState(int channelNumber, bool? closed)
    {
        _channelStates[channelNumber] = closed;
        UpdateRelayBankStatus();
    }

    private void UpdateRelayBankStatus()
    {
        foreach (var channel in _channelUis)
        {
            var state = _channelStates.TryGetValue(channel.ChannelNumber, out var value) ? value : null;
            channel.StateText.Text = state switch
            {
                true => T("state.connected"),
                false => T("state.disconnected"),
                _ => T("state.unknown")
            };
        }

        RelayBankStatusText.Text = string.Join("  |  ", _channelUis.Select(channel =>
        {
            var state = _channelStates.TryGetValue(channel.ChannelNumber, out var value) ? value : null;
            var label = state switch
            {
                true => T("state.connected"),
                false => T("state.disconnected"),
                _ => T("state.unknown")
            };

            return $"CH{channel.ChannelNumber}: {label}";
        }));
    }

    private void SetStatus(MonitorStatus status)
    {
        _currentStatus = status;
        StatusText.Text = T($"status.{status}");

        switch (status)
        {
            case MonitorStatus.Tripped:
                StatusBadgeBorder.Background = new SolidColorBrush(Color.FromRgb(254, 226, 226));
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(153, 27, 27));
                TemperatureText.Foreground = new SolidColorBrush(Color.FromRgb(153, 27, 27));
                break;
            case MonitorStatus.Monitoring:
                StatusBadgeBorder.Background = new SolidColorBrush(Color.FromRgb(220, 252, 231));
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
                TemperatureText.Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42));
                break;
            case MonitorStatus.NoReading:
                StatusBadgeBorder.Background = new SolidColorBrush(Color.FromRgb(254, 249, 195));
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(133, 77, 14));
                TemperatureText.Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42));
                break;
            default:
                StatusBadgeBorder.Background = new SolidColorBrush(Color.FromRgb(226, 232, 240));
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42));
                TemperatureText.Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42));
                break;
        }
    }

    private async Task RefreshGasFlowAsync(bool force = false)
    {
        if (_isRefreshingGasFlow || _settings is null)
        {
            return;
        }

        if (!_settings.Amc2100.Enabled)
        {
            _lastGasFlowMlMin = null;
            UpdateGasFlowDisplay();
            return;
        }

        if (!force && !ValidateAmcPortExistsSilently())
        {
            _lastGasFlowMlMin = null;
            UpdateGasFlowDisplay();
            return;
        }

        _isRefreshingGasFlow = true;
        try
        {
            _lastGasFlowMlMin = await CreateGasFlowController().ReadActualFlowAsync(CancellationToken.None);
        }
        catch
        {
            _lastGasFlowMlMin = null;
        }
        finally
        {
            _isRefreshingGasFlow = false;
            UpdateGasFlowDisplay();
        }
    }

    private bool ValidateAmcPortExistsSilently()
    {
        var portName = _settings.Amc2100.PortName?.Trim();
        if (string.IsNullOrWhiteSpace(portName))
        {
            return false;
        }

        return SerialPort.GetPortNames().Contains(portName, StringComparer.OrdinalIgnoreCase);
    }

    private void UpdateGasFlowDisplay()
    {
        if (_settings is null)
        {
            GasFlowText.Text = "--";
            return;
        }

        if (!_settings.Amc2100.Enabled)
        {
            GasFlowText.Text = T("gasFlow.disabled");
            GasFlowText.FontSize = 24;
            return;
        }

        if (_lastGasFlowMlMin is null)
        {
            GasFlowText.Text = T("gasFlow.unavailable");
            GasFlowText.FontSize = 24;
            return;
        }

        GasFlowText.Text = $"{_lastGasFlowMlMin.Value:0.0} {T("gasFlow.unit")}";
        GasFlowText.FontSize = 26;
    }

    private void SetCurrentMode(string text)
    {
        CurrentModeText.Text = text;
    }

    private async Task RebuildOrRestartMonitoringAsync()
    {
        if (_monitoringCts is null)
        {
            BuildServices();
            return;
        }

        if (!ValidateMonitoringSetup())
        {
            return;
        }

        _monitoringCts.Cancel();
        _monitoringCts.Dispose();
        _monitoringCts = new CancellationTokenSource();
        BuildServices();
        SetStatus(MonitorStatus.Monitoring);
        SetCurrentMode(T("mode.monitoring"));
        FooterText.Text = T("footer.monitoringRestarted");
        _ = Task.Run(() => RunMonitoringLoopAsync(_monitoringCts.Token));
        await RefreshGasFlowAsync(force: true);
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
        MessageBox.Show(this, ex.Message, T("app.title"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ShowSetupWarning(string message)
    {
        FooterText.Text = message;
        MessageBox.Show(this, message, T("message.setupRequiredTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private sealed record ChannelUi(
        int ChannelNumber,
        TextBlock NameText,
        TextBlock MappingText,
        TextBlock StateText,
        Button OpenButton,
        Button CloseButton);
}
