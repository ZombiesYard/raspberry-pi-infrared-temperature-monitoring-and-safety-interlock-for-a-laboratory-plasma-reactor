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
using ReactorSoftInterlock.Application.G2000;
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
    private IRelayBankController? _relayBankController;
    private IG2000Controller? _g2000Controller;
    private CancellationTokenSource? _monitoringCts;
    private readonly DispatcherTimer _gasFlowTimer = new();
    private double? _lastTemperatureC;
    private double? _lastGasFlowMlMin;
    private MonitorStatus _currentStatus = MonitorStatus.Idle;
    private G2000TelemetrySnapshot _lastG2000Snapshot = new();
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
        try
        {
            _settingsStore = new SettingsStore(_settingsPath);
            _settings = await _settingsStore.LoadAsync(CancellationToken.None);
            InitializeG2000OptionLists();
            LanguageBox.ItemsSource = new[]
            {
                new LanguageOption("en", "English"),
                new LanguageOption("zh-CN", "中文"),
                new LanguageOption("de", "Deutsch")
            };

            BindSettingsToUi();
            ApplyLanguage();
            BuildServices();
            await InitializeG2000ControllerAsync();
            SetAllChannelStates(null);
            SetCurrentMode(T("mode.monitoring"));
            UpdateGasFlowDisplay();
            _gasFlowTimer.Start();
            await LoadRecentHistoryAsync();
            DrawTemperatureChart();
            Activate();
        }
        catch (Exception ex)
        {
            FooterText.Text = ex.Message;
            MessageBox.Show(this, ex.ToString(), "ReactorSoftInterlock startup", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _gasFlowTimer.Stop();
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
        if (_g2000Controller is not null)
        {
            _g2000Controller.TelemetryUpdated -= G2000Controller_TelemetryUpdated;
        }

        (_relayBankController as IDisposable)?.Dispose();
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

    private void InitializeG2000OptionLists()
    {
        G2000RecoveryPolicyBox.ItemsSource = Enum.GetValues<TripRecoveryPolicy>()
            .Select(static policy => new EnumOption<TripRecoveryPolicy>(policy, policy.ToString()))
            .ToList();
        G2000RecoveryPolicyBox.DisplayMemberPath = nameof(EnumOption<TripRecoveryPolicy>.DisplayName);
        G2000RecoveryPolicyBox.SelectedValuePath = nameof(EnumOption<TripRecoveryPolicy>.Value);
    }

    private async Task InitializeG2000ControllerAsync()
    {
        if (_g2000Controller is null)
        {
            UpdateG2000ModeAvailability();
            UpdateG2000Telemetry(_lastG2000Snapshot);
            return;
        }

        await _g2000Controller.EnsureConnectedAsync(CancellationToken.None);
        UpdateG2000ModeAvailability();
        UpdateG2000Telemetry(_g2000Controller.Snapshot);
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
            if (_monitoringCts is null)
            {
                BuildServices();
            }
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

            await _relayBankController!.OpenAllInterlocksAsync(CancellationToken.None);
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

            await _relayBankController!.CloseAllInterlocksAsync(CancellationToken.None);
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

            await _relayBankController!.SetChannelClosedAsync(channelNumber, closed, CancellationToken.None);
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

            await _relayBankController!.CloseAllInterlocksAsync(CancellationToken.None);
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

    private async void G2000RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsFromUiAsync();
            var controller = await EnsureG2000ControlAsync();
            if (controller is null)
            {
                return;
            }

            UpdateG2000Telemetry(controller.Snapshot);
            FooterText.Text = "G2000 CAN session refreshed.";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private async void G2000HvAusButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            await controller.SetHvStateAsync(G2000HvState.HvAus, CancellationToken.None);
            FooterText.Text = "G2000 set to HV AUS.";
        });
    }

    private async void G2000HvReadyButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            if (TryWarnAboutPendingG2000Setpoints(controller))
            {
                return;
            }

            await controller.SetHvStateAsync(G2000HvState.HvReady, CancellationToken.None);
            FooterText.Text = "G2000 set to HV bereit.";
        });
    }

    private async void G2000HvEinButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            if (TryWarnAboutPendingG2000Setpoints(controller))
            {
                return;
            }

            var needsLead = !controller.Snapshot.HvEnable && _settings.Relay.G2000Can.HvReadyLeadTimeMs > 0;
            await controller.SetHvStateAsync(G2000HvState.HvOn, CancellationToken.None);
            FooterText.Text = needsLead
                ? $"HV EIN armed. Holding HV bereit for {_settings.Relay.G2000Can.HvReadyLeadTimeMs} ms first."
                : "G2000 set to HV EIN.";
        });
    }

    private async void G2000ApplySetpointsButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            await SaveSettingsFromUiAsync();
            await controller.ApplyWritableSetpointsAsync(_settings.Relay.G2000Can.WritableSetpoints.Clone(), CancellationToken.None);
            FooterText.Text = "G2000 U2 / inverter / pulse setpoints applied.";
        });
    }

    private async void G2000ManualTwoStepButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            await SaveSettingsFromUiAsync();
            await controller.StartAutomaticSequenceAsync(_settings.Relay.G2000Can.StartupRecipe.Clone(), CancellationToken.None);
            FooterText.Text = "Verified G2000 startup recipe started. U2 will stay inside the configured software range unless AllowUnsafeU2Writes is enabled.";
        });
    }

    private async void G2000StartAutomaticButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            await SaveSettingsFromUiAsync();
            await controller.StartAutomaticSequenceAsync(_settings.Relay.G2000Can.StartupRecipe.Clone(), CancellationToken.None);
            FooterText.Text = "Automatic G2000 startup recipe started.";
        });
    }

    private async void G2000StopAutomaticButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            await controller.StopAutomaticSequenceAsync(CancellationToken.None);
            FooterText.Text = "Automatic G2000 recipe stopped.";
        });
    }

    private async void G2000ResetTripButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            await controller.ResetAsync(CancellationToken.None);
            FooterText.Text = "G2000 recovery command applied.";
        });
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
        if (_monitoringService is not null)
        {
            _monitoringService.SampleRecorded -= MonitoringService_SampleRecorded;
        }

        if (_g2000Controller is not null)
        {
            _g2000Controller.TelemetryUpdated -= G2000Controller_TelemetryUpdated;
        }

        (_relayBankController as IDisposable)?.Dispose();
        _g2000Controller = null;
        _settings.Relay.Normalize();
        _settings.Amc2100.Normalize();
        var dataDirectory = Path.IsPathRooted(_settings.DataDirectory)
            ? _settings.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, _settings.DataDirectory);

        _sampleLog = new CsvSampleLog(Path.Combine(dataDirectory, "temperature-history.csv"));
        _stateMachine = new InterlockStateMachine(new InterlockSettings(_settings.ThresholdC));
        var reader = new TesseractCliTemperatureReader(new WindowCapture(), new TemperatureTextParser(), _settings);
        var autoReset = new AutoResetOptions(_settings.AutoResetEnabled, _settings.RecoveryThresholdC, _settings.RecoveryStableSeconds);
        _relayBankController = CreateRelayBank();
        _g2000Controller = _relayBankController as IG2000Controller;
        if (_g2000Controller is not null)
        {
            _g2000Controller.TelemetryUpdated += G2000Controller_TelemetryUpdated;
        }

        _monitoringService = new MonitoringService(reader, CreateProcessOutputController(), _sampleLog, new SystemClock(), _stateMachine, autoReset);
        _monitoringService.SampleRecorded += MonitoringService_SampleRecorded;
        UpdateGasFlowDisplay();
        UpdateG2000ModeAvailability();
    }

    private IRelayBankController CreateRelayBank()
    {
        return _settings.Relay.ResolveMode() switch
        {
            RelayControllerMode.DryRun => new DryRunRelayController(),
            RelayControllerMode.Serial => new SerialRelayController(_settings.Relay),
            RelayControllerMode.G2000Can => new HybridG2000InterlockController(
                new G2000CanController(_settings.Relay.G2000Can),
                CreatePhysicalInterlockRelay()),
            _ => throw new InvalidOperationException($"Unsupported relay controller mode '{_settings.Relay.Mode}'.")
        };
    }

    private IRelayBankController CreatePhysicalInterlockRelay()
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
        return new ProcessOutputController(_relayBankController!);
    }

    private async Task<IG2000Controller?> EnsureG2000ControlAsync()
    {
        if (_settings.Relay.ResolveMode() != RelayControllerMode.G2000Can)
        {
            ShowSetupWarning("Relay.Mode must be G2000Can before using the G2000 software console.");
            return null;
        }

        if (_monitoringCts is not null)
        {
            ShowSetupWarning("Stop OCR monitoring before manual G2000 control in this build. 先停止监控，再做手动 G2000 控制。");
            return null;
        }

        if (!ValidateG2000CanSetup())
        {
            return null;
        }

        if (_g2000Controller is null)
        {
            BuildServices();
        }

        await _g2000Controller!.EnsureConnectedAsync(CancellationToken.None);
        return _g2000Controller;
    }

    private async Task ExecuteG2000Async(Func<IG2000Controller, Task> action)
    {
        try
        {
            var controller = await EnsureG2000ControlAsync();
            if (controller is null)
            {
                return;
            }

            await action(controller);
            UpdateG2000Telemetry(controller.Snapshot);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
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

        return _settings.Relay.ResolveMode() switch
        {
            RelayControllerMode.DryRun => true,
            RelayControllerMode.Serial => ValidateSerialRelaySetup(requireRestore, specificChannelNumber),
            RelayControllerMode.G2000Can => ValidateG2000CanSetup() && ValidatePhysicalInterlockSetupForCanMode(requireRestore, specificChannelNumber),
            _ => false
        };
    }

    private bool ValidatePhysicalInterlockSetupForCanMode(bool requireRestore, int? specificChannelNumber)
    {
        return _settings.Relay.DryRun || ValidateSerialRelaySetup(requireRestore, specificChannelNumber);
    }

    private bool ValidateSerialRelaySetup(bool requireRestore, int? specificChannelNumber)
    {
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

    private bool ValidateG2000CanSetup()
    {
        if (!PcanChannelParser.TryParse(_settings.Relay.G2000Can.Channel, out _))
        {
            ShowSetupWarning("G2000 CAN channel must look like UsbBus1, UsbBus2, or PCAN_USBBUS1.");
            return false;
        }

        if (_settings.Relay.G2000Can.NodeId > 0x7E)
        {
            ShowSetupWarning("G2000 CAN node ID must be in the range 0x00..0x7E.");
            return false;
        }

        if (_settings.Relay.G2000Can.CommandPeriodMs <= 0)
        {
            ShowSetupWarning("G2000 CAN command period must be a positive number of milliseconds.");
            return false;
        }

        if (_settings.Relay.G2000Can.ReadPollIntervalMs <= 0)
        {
            ShowSetupWarning("G2000 CAN read poll interval must be a positive number of milliseconds.");
            return false;
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

    private void G2000Controller_TelemetryUpdated(object? sender, G2000TelemetrySnapshot snapshot)
    {
        Dispatcher.Invoke(() => UpdateG2000Telemetry(snapshot));
    }

    private void UpdateG2000Telemetry(G2000TelemetrySnapshot snapshot)
    {
        _lastG2000Snapshot = snapshot.Clone();
        G2000ConnectionText.Text = snapshot.Connected
            ? snapshot.CommunicationHealthy ? "Connected / healthy" : "Connected / waiting for fresh frames"
            : "Not connected";
        G2000SourceText.Text = $"{snapshot.Source} / {DescribeG2000HvState(snapshot)}";
        G2000FaultText.Text = snapshot.Fault
            ? $"{snapshot.ErrorText} (0x{snapshot.ErrorCode:X2})"
            : "No fault";
        G2000TripText.Text = snapshot.TripLatched
            ? snapshot.TripReason
            : "No latched trip";
        G2000ModeText.Text = snapshot.UiMode.ToString();
        G2000AutoStageText.Text = snapshot.AutomaticStage;

        G2000TargetVoltageText.Text = snapshot.TargetSetpoints.VoltageV.ToString("0.0", CultureInfo.InvariantCulture);
        G2000TargetFrequencyText.Text = snapshot.TargetSetpoints.FrequencyKhz.ToString("0.0", CultureInfo.InvariantCulture);
        G2000TargetDutyText.Text = snapshot.TargetSetpoints.DutyPercent.ToString("0.0", CultureInfo.InvariantCulture);
        G2000TargetTonText.Text = snapshot.TargetSetpoints.TonMs.ToString("0.000", CultureInfo.InvariantCulture);
        G2000TargetToffText.Text = snapshot.TargetSetpoints.ToffMs.ToString("0.000", CultureInfo.InvariantCulture);
        G2000ActualVoltageText.Text = FormatNullableDouble(snapshot.DcLinkVoltageV, "0.000");
        G2000ActualFrequencyText.Text = FormatNullableDouble(snapshot.FrequencyKhz, "0.000");
        G2000ActualDutyText.Text = FormatNullableDouble(snapshot.DutyPercent, "0.000");
        G2000ActualTonText.Text = FormatNullableDouble(snapshot.TonMs, "0.000");
        G2000ActualToffText.Text = FormatNullableDouble(snapshot.ToffMs, "0.000");
        G2000ActualAuxText.Text = FormatNullableDouble(snapshot.DcLinkAuxValue, "0.000");

        G2000Raw180Text.Text = snapshot.StatusFrameHex;
        G2000Raw280Text.Text = snapshot.DcLinkActualFrameHex;
        G2000Raw281Text.Text = snapshot.InverterActualFrameHex;
        G2000Raw380Text.Text = snapshot.ReservedActualFrameHex;
        G2000Raw381Text.Text = snapshot.PulseActualFrameHex;

        if (_settings?.Relay.ResolveMode() == RelayControllerMode.G2000Can)
        {
            CurrentModeText.Text = snapshot.UiMode == G2000UiMode.Automatic ? "G2000 automatic" : "G2000 manual";
        }

        UpdateRelayBankStatus();
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
        G2000VoltageBox.Text = _settings.Relay.G2000Can.WritableSetpoints.VoltageV.ToString("0.0", CultureInfo.InvariantCulture);
        G2000FrequencyBox.Text = _settings.Relay.G2000Can.WritableSetpoints.FrequencyKhz.ToString("0.0", CultureInfo.InvariantCulture);
        G2000DutyBox.Text = _settings.Relay.G2000Can.WritableSetpoints.DutyPercent.ToString("0.0", CultureInfo.InvariantCulture);
        G2000TonBox.Text = _settings.Relay.G2000Can.WritableSetpoints.TonMs.ToString("0.000", CultureInfo.InvariantCulture);
        G2000ToffBox.Text = _settings.Relay.G2000Can.WritableSetpoints.ToffMs.ToString("0.000", CultureInfo.InvariantCulture);
        G2000Stage1VoltageBox.Text = _settings.Relay.G2000Can.StartupRecipe.Stage1VoltageV.ToString("0.0", CultureInfo.InvariantCulture);
        G2000Stage1DurationBox.Text = _settings.Relay.G2000Can.StartupRecipe.Stage1DurationMs.ToString(CultureInfo.InvariantCulture);
        G2000Stage2VoltageBox.Text = _settings.Relay.G2000Can.StartupRecipe.Stage2VoltageV.ToString("0.0", CultureInfo.InvariantCulture);
        G2000Stage2HoldBox.IsChecked = _settings.Relay.G2000Can.StartupRecipe.Stage2HoldEnabled;
        G2000EnterHvReadyBox.IsChecked = _settings.Relay.G2000Can.StartupRecipe.EnterHvReadyBeforeRun;
        G2000EnterHvOnBox.IsChecked = _settings.Relay.G2000Can.StartupRecipe.EnterHvOnAtStart;
        G2000RecoveryPolicyBox.SelectedValue = _settings.Relay.G2000Can.ResolveRecoveryPolicy();
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
        _settings.Relay.G2000Can.WritableSetpoints.VoltageV = ParseDouble(G2000VoltageBox.Text, nameof(_settings.Relay.G2000Can.WritableSetpoints.VoltageV));
        _settings.Relay.G2000Can.WritableSetpoints.FrequencyKhz = ParseDouble(G2000FrequencyBox.Text, nameof(_settings.Relay.G2000Can.WritableSetpoints.FrequencyKhz));
        _settings.Relay.G2000Can.WritableSetpoints.DutyPercent = ParseDouble(G2000DutyBox.Text, nameof(_settings.Relay.G2000Can.WritableSetpoints.DutyPercent));
        _settings.Relay.G2000Can.WritableSetpoints.TonMs = ParseDouble(G2000TonBox.Text, nameof(_settings.Relay.G2000Can.WritableSetpoints.TonMs));
        _settings.Relay.G2000Can.WritableSetpoints.ToffMs = ParseDouble(G2000ToffBox.Text, nameof(_settings.Relay.G2000Can.WritableSetpoints.ToffMs));
        _settings.Relay.G2000Can.StartupRecipe.Stage1VoltageV = ParseDouble(G2000Stage1VoltageBox.Text, nameof(_settings.Relay.G2000Can.StartupRecipe.Stage1VoltageV));
        _settings.Relay.G2000Can.StartupRecipe.Stage1DurationMs = ParseInt(G2000Stage1DurationBox.Text, nameof(_settings.Relay.G2000Can.StartupRecipe.Stage1DurationMs));
        _settings.Relay.G2000Can.StartupRecipe.Stage2VoltageV = ParseDouble(G2000Stage2VoltageBox.Text, nameof(_settings.Relay.G2000Can.StartupRecipe.Stage2VoltageV));
        _settings.Relay.G2000Can.StartupRecipe.Stage2HoldEnabled = G2000Stage2HoldBox.IsChecked == true;
        _settings.Relay.G2000Can.StartupRecipe.EnterHvReadyBeforeRun = G2000EnterHvReadyBox.IsChecked == true;
        _settings.Relay.G2000Can.StartupRecipe.EnterHvOnAtStart = G2000EnterHvOnBox.IsChecked == true;
        _settings.Relay.G2000Can.RecoveryPolicy = ((TripRecoveryPolicy?)G2000RecoveryPolicyBox.SelectedValue ?? TripRecoveryPolicy.HoldHvAus).ToString();
        _settings.Relay.Normalize();
        _settings.Relay.G2000Can.ValidateWritableSetpoints(_settings.Relay.G2000Can.WritableSetpoints);
        _settings.Relay.G2000Can.ValidateStartupRecipe(_settings.Relay.G2000Can.StartupRecipe);
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

        try
        {
            _settings.Language = NormalizeLanguage(LanguageBox.SelectedValue?.ToString() ?? "en");
            ApplyLanguage();
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
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
        G2000Tab.Header = "G2000";
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

        UpdateG2000ModeAvailability();
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
        if (_settings?.Relay.ResolveMode() == RelayControllerMode.G2000Can)
        {
            var physicalFallbackText = _settings.Relay.DryRun ? "Physical interlock fallback: Dry Run" : $"Physical interlock fallback: {_settings.Relay.PortName}";
            RelayBankStatusText.Text = $"CAN {_lastG2000Snapshot.Source} | {DescribeG2000HvState(_lastG2000Snapshot)} | {(_lastG2000Snapshot.Fault ? _lastG2000Snapshot.ErrorText : "No fault")} | {physicalFallbackText}";
            foreach (var channel in _channelUis)
            {
                channel.StateText.Text = _settings.Relay.DryRun ? "CAN + relay dry run" : "Physical interlock relay";
            }

            return;
        }

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

    private void UpdateG2000ModeAvailability()
    {
        var isCanMode = _settings?.Relay.ResolveMode() == RelayControllerMode.G2000Can;
        G2000Tab.IsEnabled = isCanMode;
        var physicalRelayAvailable = isCanMode && _settings is not null && !_settings.Relay.DryRun;

        foreach (var channel in _channelUis)
        {
            channel.OpenButton.IsEnabled = !isCanMode || physicalRelayAvailable;
            channel.CloseButton.IsEnabled = !isCanMode || physicalRelayAvailable;
            string? tooltip = null;
            if (isCanMode && !physicalRelayAvailable)
            {
                tooltip = "G2000 CAN mode is active, but the physical relay fallback is still in Dry Run.";
            }
            else if (isCanMode)
            {
                tooltip = "Physical interlock relay only. This does not directly command HV EIN/AUS.";
            }

            channel.OpenButton.ToolTip = tooltip;
            channel.CloseButton.ToolTip = tooltip;
        }

        if (isCanMode)
        {
            EngineeringNotesText.Text = physicalRelayAvailable
                ? "CAN is the main G2000 control path. Engineering relay actions now operate the physical interlock fallback only."
                : "CAN is the main G2000 control path. Physical interlock fallback is currently Dry Run only, so relay actions do not move hardware.";
            TestRelayButton.Content = "断开物理 Interlock / Open Physical Interlock";
            TestRelayResetButton.Content = "闭合物理 Interlock / Close Physical Interlock";
            TripAllEngineeringButton.Content = "断开物理 Interlock / Open Physical Interlock";
            RestoreAllEngineeringButton.Content = "闭合物理 Interlock / Close Physical Interlock";
        }
        else
        {
            EngineeringNotesText.Text = T("engineering.notes");
            TestRelayButton.Content = T("button.disconnectAll");
            TestRelayResetButton.Content = T("button.connectAll");
            TripAllEngineeringButton.Content = T("button.disconnectAll");
            RestoreAllEngineeringButton.Content = T("button.connectAll");
        }
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
            await InitializeG2000ControllerAsync();
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
        await InitializeG2000ControllerAsync();
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

    private static string DescribeG2000HvState(G2000TelemetrySnapshot snapshot)
    {
        return snapshot.HvOn
            ? "HV EIN"
            : snapshot.HvEnable
                ? "HV bereit"
                : "HV AUS";
    }

    private static string FormatNullableDouble(double? value, string format)
    {
        return value?.ToString(format, CultureInfo.InvariantCulture) ?? "--";
    }

    private bool TryWarnAboutPendingG2000Setpoints(IG2000Controller controller)
    {
        var uiSetpoints = new G2000WritableSetpoints
        {
            VoltageV = ParseDouble(G2000VoltageBox.Text, nameof(_settings.Relay.G2000Can.WritableSetpoints.VoltageV)),
            FrequencyKhz = ParseDouble(G2000FrequencyBox.Text, nameof(_settings.Relay.G2000Can.WritableSetpoints.FrequencyKhz)),
            DutyPercent = ParseDouble(G2000DutyBox.Text, nameof(_settings.Relay.G2000Can.WritableSetpoints.DutyPercent)),
            TonMs = ParseDouble(G2000TonBox.Text, nameof(_settings.Relay.G2000Can.WritableSetpoints.TonMs)),
            ToffMs = ParseDouble(G2000ToffBox.Text, nameof(_settings.Relay.G2000Can.WritableSetpoints.ToffMs))
        };

        var applied = controller.TargetSetpoints;
        var pending =
            Math.Abs(uiSetpoints.VoltageV - applied.VoltageV) > 0.0001 ||
            Math.Abs(uiSetpoints.FrequencyKhz - applied.FrequencyKhz) > 0.0001 ||
            Math.Abs(uiSetpoints.DutyPercent - applied.DutyPercent) > 0.0001 ||
            Math.Abs(uiSetpoints.TonMs - applied.TonMs) > 0.0001 ||
            Math.Abs(uiSetpoints.ToffMs - applied.ToffMs) > 0.0001;

        if (!pending)
        {
            return false;
        }

        ShowSetupWarning("The setpoints in the text boxes have not been sent yet. Click 'Apply Setpoints / 写入设定值' first. 输入框里的设定值还没有下发，请先点“写入设定值”。");
        return true;
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

    private sealed record EnumOption<T>(T Value, string DisplayName) where T : struct, Enum;
}
