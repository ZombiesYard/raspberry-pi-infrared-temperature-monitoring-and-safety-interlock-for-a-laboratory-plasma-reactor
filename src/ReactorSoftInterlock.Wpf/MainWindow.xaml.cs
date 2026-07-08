using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Reflection;
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
    private DateTimeOffset? _lastTemperatureSampleAt;
    private DateTimeOffset? _monitoringStartedAt;
    private double? _lastGasFlowMlMin;
    private MonitorStatus _currentStatus = MonitorStatus.Idle;
    private G2000TelemetrySnapshot _lastG2000Snapshot = new();
    private string? _lastG2000ConnectionError;
    private string? _relayRuntimeKey;
    private readonly SemaphoreSlim _branchInterlockGate = new(1, 1);
    private bool _isBindingSettings;
    private bool _isRefreshingGasFlow;
    private double _monitorLeftScrollOffset;
    private bool _isRestoringMonitorLeftScroll;

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
        G2000RecoveryPolicyBox.DisplayMemberPath = nameof(EnumOption<TripRecoveryPolicy>.DisplayName);
        G2000RecoveryPolicyBox.SelectedValuePath = nameof(EnumOption<TripRecoveryPolicy>.Value);
        var wasBindingSettings = _isBindingSettings;
        _isBindingSettings = true;
        RefreshG2000RecoveryPolicyOptions();
        _isBindingSettings = wasBindingSettings;
    }

    private void RefreshG2000RecoveryPolicyOptions()
    {
        var selected = (TripRecoveryPolicy?)G2000RecoveryPolicyBox.SelectedValue ??
            _settings?.Relay.G2000Can.ResolveRecoveryPolicy() ??
            TripRecoveryPolicy.HoldHvAus;
        G2000RecoveryPolicyBox.ItemsSource = Enum.GetValues<TripRecoveryPolicy>()
            .Select(policy => new EnumOption<TripRecoveryPolicy>(policy, FormatRecoveryPolicy(policy)))
            .ToList();
        G2000RecoveryPolicyBox.SelectedValue = selected;
    }

    private async Task InitializeG2000ControllerAsync()
    {
        if (_g2000Controller is null)
        {
            UpdateG2000ModeAvailability();
            UpdateG2000Telemetry(_lastG2000Snapshot);
            UpdateG2000SettingsSummary();
            return;
        }

        try
        {
            await _g2000Controller.EnsureConnectedAsync(CancellationToken.None);
            _lastG2000ConnectionError = null;
            UpdateG2000Telemetry(_g2000Controller.Snapshot);
        }
        catch (InvalidOperationException ex) when (IsPcanConnectionError(ex))
        {
            _lastG2000ConnectionError = ex.Message;
            UpdateG2000Telemetry(_g2000Controller.Snapshot);
            FooterText.Text = string.Format(CultureInfo.InvariantCulture, T("footer.g2000CanNotConnected"), ex.Message);
        }

        UpdateG2000ModeAvailability();
        UpdateG2000SettingsSummary();
    }

    private async void SelectRoiButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SaveSettingsFromUiAsync();
            var capture = new WindowCapture();
            var windowBounds = capture.GetWindowBounds(_settings.WindowTitleContains);
            if (!capture.BringWindowToForeground(_settings.WindowTitleContains))
            {
                ShowSetupWarning(T("message.hikmicroForegroundFailed"));
                return;
            }

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
                await RebuildServicesPreservingG2000Async();
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

            await RebuildServicesPreservingG2000Async();
            StartMonitoringLoop(T("footer.monitoringStarted"));
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
            if (!ValidateBranchInterlockSetup(requireRestore: false))
            {
                return;
            }

            await ExecuteBranchInterlockCommandAsync(static controller => controller.OpenAllInterlocksAsync(CancellationToken.None));
            SetAllChannelStates(false);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = FormatBranchInterlockFooter(T("footer.allDisconnected"));
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
            if (!ValidateBranchInterlockSetup(requireRestore: true))
            {
                return;
            }

            await ExecuteBranchInterlockCommandAsync(static controller => controller.CloseAllInterlocksAsync(CancellationToken.None));
            SetAllChannelStates(true);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = FormatBranchInterlockFooter(T("footer.allConnected"));
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

            if (!ValidateBranchInterlockSetup(requireRestore: true, specificChannelNumber: channelNumber))
            {
                return;
            }

            await ExecuteBranchInterlockCommandAsync(controller => controller.SetChannelClosedAsync(channelNumber, closed, CancellationToken.None));
            SetChannelState(channelNumber, closed);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = FormatBranchInterlockFooter(string.Format(CultureInfo.InvariantCulture, footerTemplate, channelNumber));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void OpenGuideButton_Click(object sender, RoutedEventArgs e)
    {
        var guide = new GuideWindow(T("guide.title"), T("guide.body"), T("button.close"), T("tooltip.guideText"), T("tooltip.closeGuide"))
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
                Filter = T("dialog.csvFilter"),
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

    private async void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _sampleLog.ClearAsync(CancellationToken.None);
            _history.Clear();
            DrawTemperatureChart();
            FooterText.Text = T("footer.historyCleared");
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
            if (!ValidateBranchInterlockSetup(requireRestore: true))
            {
                return;
            }

            await ExecuteBranchInterlockCommandAsync(static controller => controller.CloseAllInterlocksAsync(CancellationToken.None));
            SetAllChannelStates(true);
            SetCurrentMode(T("mode.monitoring"));
            MainTabControl.SelectedItem = MonitorTab;
            FooterText.Text = FormatBranchInterlockFooter(T("footer.monitoringControlRestored"));
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
            await SaveG2000SettingsFromUiAsync();
            var controller = await EnsureG2000ControlAsync();
            if (controller is null)
            {
                return;
            }

            UpdateG2000Telemetry(controller.Snapshot);
            FooterText.Text = T("footer.g2000Refreshed");
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
            FooterText.Text = T("footer.g2000HvAus");
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
            FooterText.Text = T("footer.g2000HvReady");
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
                ? string.Format(CultureInfo.InvariantCulture, T("footer.g2000HvEinArmed"), _settings.Relay.G2000Can.HvReadyLeadTimeMs)
                : T("footer.g2000HvEin");
        });
    }

    private async void G2000ApplySetpointsButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            await ApplyG2000WritableSetpointsFromUiAsync(controller, T("footer.g2000SetpointsApplied"));
        });
    }

    private async void G2000SetpointStepButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            if (sender is not FrameworkElement { Tag: string tag })
            {
                return;
            }

            var parts = tag.Split(':', 2);
            if (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
            {
                return;
            }

            var textBox = GetG2000WritableSetpointBox(parts[0], out var format);
            var current = ParseDouble(textBox.Text, parts[0]);
            textBox.Text = (current + delta).ToString(format, CultureInfo.InvariantCulture);
            await ApplyG2000WritableSetpointsFromUiAsync(controller, T("footer.g2000SetpointAdjusted"));
        });
    }

    private async void G2000StartAutomaticButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            if (!ValidateTemperatureMonitorReadyForG2000Automatic())
            {
                return;
            }

            await SaveG2000SettingsFromUiAsync();
            await controller.StartAutomaticSequenceAsync(_settings.Relay.G2000Can.StartupRecipe.Clone(), CancellationToken.None);
            FooterText.Text = T("footer.g2000AutomaticStarted");
        });
    }

    private async void G2000StopAutomaticButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async(async controller =>
        {
            await controller.StopAutomaticSequenceAsync(CancellationToken.None);
            FooterText.Text = T("footer.g2000AutomaticStopped");
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
        DetachMonitoringService();

        if (_g2000Controller is not null)
        {
            _g2000Controller.TelemetryUpdated -= G2000Controller_TelemetryUpdated;
        }

        (_relayBankController as IDisposable)?.Dispose();
        _g2000Controller = null;
        _settings.Relay.Normalize();
        _settings.Amc2100.Normalize();
        var relayRuntimeKey = CreateRelayRuntimeKey(_settings.Relay);
        _relayBankController = CreateRelayBank();
        _relayRuntimeKey = relayRuntimeKey;
        _g2000Controller = _relayBankController as IG2000Controller;
        if (_g2000Controller is not null)
        {
            _g2000Controller.TelemetryUpdated += G2000Controller_TelemetryUpdated;
        }

        BuildMonitoringService();
        UpdateGasFlowDisplay();
        UpdateG2000ModeAvailability();
    }

    private void BuildMonitoringService()
    {
        DetachMonitoringService();
        _settings.Relay.Normalize();
        _settings.Amc2100.Normalize();
        if (_relayBankController is null)
        {
            _relayBankController = CreateRelayBank();
            _relayRuntimeKey = CreateRelayRuntimeKey(_settings.Relay);
            _g2000Controller = _relayBankController as IG2000Controller;
            if (_g2000Controller is not null)
            {
                _g2000Controller.TelemetryUpdated += G2000Controller_TelemetryUpdated;
            }
        }

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
        UpdateG2000ModeAvailability();
    }

    private void DetachMonitoringService()
    {
        if (_monitoringService is not null)
        {
            _monitoringService.SampleRecorded -= MonitoringService_SampleRecorded;
            _monitoringService = null;
        }
    }

    private async Task RebuildServicesPreservingG2000Async()
    {
        if (_settings.Relay.ResolveMode() == RelayControllerMode.G2000Can &&
            _g2000Controller is not null &&
            string.Equals(_relayRuntimeKey, CreateRelayRuntimeKey(_settings.Relay), StringComparison.Ordinal))
        {
            BuildMonitoringService();
            await InitializeG2000ControllerAsync();
            return;
        }

        BuildServices();
        await InitializeG2000ControllerAsync();
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

    private IRelayBankController CreateBranchInterlockRelay()
    {
        _settings.Relay.Normalize();

        return _settings.Relay.ResolveMode() switch
        {
            RelayControllerMode.DryRun => new DryRunRelayController(),
            RelayControllerMode.Serial => new SerialRelayController(_settings.Relay),
            RelayControllerMode.G2000Can => CreatePhysicalInterlockRelay(),
            _ => throw new InvalidOperationException($"Unsupported relay controller mode '{_settings.Relay.Mode}'.")
        };
    }

    private async Task ExecuteBranchInterlockCommandAsync(Func<IRelayBankController, Task> command)
    {
        await _branchInterlockGate.WaitAsync(CancellationToken.None);
        IRelayBankController? branchInterlock = null;
        try
        {
            branchInterlock = CreateBranchInterlockRelay();
            await command(branchInterlock);
        }
        finally
        {
            (branchInterlock as IDisposable)?.Dispose();
            _branchInterlockGate.Release();
        }
    }

    private static string CreateRelayRuntimeKey(RelaySettings settings)
    {
        var channelKey = string.Join(
            "|",
            settings.Channels
                .OrderBy(static channel => channel.ChannelNumber)
                .Select(static channel => string.Join(
                    ",",
                    channel.ChannelNumber.ToString(CultureInfo.InvariantCulture),
                    channel.Enabled.ToString(),
                    channel.OpenCommand,
                    channel.CloseCommand)));

        return string.Join(
            "\u001F",
            settings.ResolveMode().ToString(),
            settings.DryRun.ToString(),
            settings.PortName,
            settings.BaudRate.ToString(CultureInfo.InvariantCulture),
            settings.G2000Can.Channel,
            settings.G2000Can.NodeId.ToString(CultureInfo.InvariantCulture),
            settings.G2000Can.CommandPeriodMs.ToString(CultureInfo.InvariantCulture),
            settings.G2000Can.ReadPollIntervalMs.ToString(CultureInfo.InvariantCulture),
            channelKey);
    }

    private IGasFlowController CreateGasFlowController()
    {
        return !_settings.Amc2100.Enabled
            ? new NoOpGasFlowController()
            : new Amc2100GasFlowController(_settings.Amc2100);
    }

    private IRelayBankController CreateProcessOutputController()
    {
        return new ProcessOutputController(new SynchronizedRelayBankController(_relayBankController!, _branchInterlockGate));
    }

    private async Task<IG2000Controller?> EnsureG2000ControlAsync()
    {
        if (_settings.Relay.ResolveMode() != RelayControllerMode.G2000Can)
        {
            ShowSetupWarning(T("message.g2000ModeRequired"));
            return null;
        }

        if (!ValidateG2000CanSetup())
        {
            return null;
        }

        if (_g2000Controller is null)
        {
            if (_monitoringCts is not null)
            {
                ShowSetupWarning(T("message.g2000Unavailable"));
                return null;
            }

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

    private bool ValidateTemperatureMonitorReadyForG2000Automatic()
    {
        if (_monitoringCts is null)
        {
            ShowSetupWarning(T("message.g2000AutomaticNeedsTemperatureMonitor"));
            return false;
        }

        if (_currentStatus == MonitorStatus.Tripped || _stateMachine.IsTripped)
        {
            ShowSetupWarning(T("message.g2000AutomaticBlockedByTrip"));
            return false;
        }

        if (_currentStatus != MonitorStatus.Monitoring ||
            _lastTemperatureC is null ||
            _lastTemperatureSampleAt is null ||
            _monitoringStartedAt is null)
        {
            ShowSetupWarning(T("message.g2000AutomaticNeedsValidTemperature"));
            return false;
        }

        if (_lastTemperatureSampleAt.Value < _monitoringStartedAt.Value)
        {
            ShowSetupWarning(T("message.g2000AutomaticNeedsFreshTemperature"));
            return false;
        }

        var maxSampleAge = TimeSpan.FromMilliseconds(Math.Max(5000, _settings.PollIntervalMs * 3));
        if (DateTimeOffset.Now - _lastTemperatureSampleAt.Value > maxSampleAge)
        {
            ShowSetupWarning(T("message.g2000AutomaticNeedsFreshTemperature"));
            return false;
        }

        return true;
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

        if (_settings.PollIntervalMs <= 0)
        {
            ShowSetupWarning(T("message.pollIntervalInvalid"));
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

        if (!ValidateMonitoringRelaySetup(requireRestore: true))
        {
            return false;
        }

        if (_settings.Relay.ResolveMode() == RelayControllerMode.G2000Can && _settings.Relay.DryRun)
        {
            ShowSetupWarning(T("message.g2000LiveMonitoringNeedsRelay"));
            return false;
        }

        return true;
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

    private bool ValidateMonitoringRelaySetup(bool requireRestore, int? specificChannelNumber = null)
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

    private bool ValidateBranchInterlockSetup(bool requireRestore, int? specificChannelNumber = null)
    {
        _settings.Relay.Normalize();

        return _settings.Relay.ResolveMode() switch
        {
            RelayControllerMode.DryRun => true,
            RelayControllerMode.Serial => ValidateSerialRelaySetup(requireRestore, specificChannelNumber),
            RelayControllerMode.G2000Can => ValidatePhysicalInterlockSetupForCanMode(requireRestore, specificChannelNumber),
            _ => false
        };
    }

    private bool ValidatePhysicalInterlockSetupForCanMode(bool requireRestore, int? specificChannelNumber)
    {
        if (_settings.Relay.DryRun)
        {
            ShowSetupWarning(T("message.branchRelayDryRun"));
            return false;
        }

        return ValidateSerialRelaySetup(requireRestore, specificChannelNumber);
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
            ShowSetupWarning(T("message.g2000CanChannelInvalid"));
            return false;
        }

        if (_settings.Relay.G2000Can.NodeId > 0x7E)
        {
            ShowSetupWarning(T("message.g2000NodeInvalid"));
            return false;
        }

        if (_settings.Relay.G2000Can.CommandPeriodMs <= 0)
        {
            ShowSetupWarning(T("message.g2000CommandPeriodInvalid"));
            return false;
        }

        if (_settings.Relay.G2000Can.ReadPollIntervalMs <= 0)
        {
            ShowSetupWarning(T("message.g2000ReadPollInvalid"));
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
            _lastTemperatureSampleAt = sample.Timestamp;
            _history.Insert(0, sample);
            while (_history.Count > 500)
            {
                _history.RemoveAt(_history.Count - 1);
            }

            TemperatureText.Text = sample.TemperatureC is null
                ? T("status.NoReading").ToUpperInvariant()
                : $"{sample.TemperatureC.Value:0.0} C";
            RawOcrText.Text = string.IsNullOrWhiteSpace(sample.RawOcrText) ? T("ocr.empty") : sample.RawOcrText;
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
        var settings = _settings;
        var isCanMode = settings.Relay.ResolveMode() == RelayControllerMode.G2000Can;
        G2000ConnectionText.Text = isCanMode
            ? snapshot.Connected
                ? snapshot.CommunicationHealthy ? T("g2000.connectionHealthy") : T("g2000.connectionWaiting")
                : string.IsNullOrWhiteSpace(_lastG2000ConnectionError)
                    ? string.Format(CultureInfo.InvariantCulture, T("g2000.notConnected"), settings.Relay.G2000Can.Channel)
                    : string.Format(CultureInfo.InvariantCulture, T("g2000.notConnectedWithError"), settings.Relay.G2000Can.Channel, _lastG2000ConnectionError)
            : string.Format(CultureInfo.InvariantCulture, T("g2000.disabledByRelayMode"), settings.Relay.Mode);
        G2000DiagnosticStatusText.Text = G2000ConnectionText.Text;
        G2000SourceText.Text = $"{FormatG2000ControlSource(snapshot.Source)} / {DescribeG2000HvState(snapshot)}";
        G2000FaultText.Text = FormatG2000Fault(snapshot);
        G2000TripText.Text = snapshot.TripLatched
            ? FormatG2000TripReason(snapshot.TripReason)
            : T("g2000.noLatchedTrip");
        G2000ModeText.Text = FormatG2000UiMode(snapshot.UiMode);
        G2000AutoStageText.Text = FormatG2000Stage(snapshot.AutomaticStage);

        G2000TargetVoltageText.Text = snapshot.TargetSetpoints.VoltageV.ToString("0.0", CultureInfo.InvariantCulture);
        G2000TargetFrequencyText.Text = snapshot.TargetSetpoints.FrequencyKhz.ToString("0.0", CultureInfo.InvariantCulture);
        G2000TargetDutyText.Text = snapshot.TargetSetpoints.DutyPercent.ToString("0.0", CultureInfo.InvariantCulture);
        G2000TargetTonText.Text = snapshot.TargetSetpoints.TonMs.ToString("0.000", CultureInfo.InvariantCulture);
        G2000TargetToffText.Text = snapshot.TargetSetpoints.ToffMs.ToString("0.000", CultureInfo.InvariantCulture);
        G2000ActualVoltageText.Text = FormatNullableDouble(snapshot.DcLinkVoltageV, "0.000");
        G2000ActualDcLinkCurrentText.Text = FormatNullableDouble(snapshot.ReservedDcLinkCurrentA, "0.000");
        G2000ActualFrequencyText.Text = FormatNullableDouble(snapshot.FrequencyKhz, "0.000");
        G2000ActualDutyText.Text = FormatNullableDouble(snapshot.DutyPercent, "0.000");
        G2000ActualTonText.Text = FormatNullableDouble(snapshot.TonMs, "0.000");
        G2000ActualToffText.Text = FormatNullableDouble(snapshot.ToffMs, "0.000");
        G2000ReservedOutputVoltageText.Text = FormatNullableDouble(snapshot.ReservedOutputVoltageV, "0.000");
        G2000ReservedOutputCurrentText.Text = FormatNullableDouble(snapshot.ReservedOutputCurrentA, "0.000");

        G2000Raw180Text.Text = snapshot.StatusFrameHex;
        G2000Raw280Text.Text = snapshot.DcLinkActualFrameHex;
        G2000Raw281Text.Text = snapshot.InverterActualFrameHex;
        G2000Raw380Text.Text = snapshot.ReservedActualFrameHex;
        G2000Raw381Text.Text = snapshot.PulseActualFrameHex;

        if (_settings?.Relay.ResolveMode() == RelayControllerMode.G2000Can)
        {
            CurrentModeText.Text = FormatG2000UiMode(snapshot.UiMode);
        }

        UpdateRelayBankStatus();
        UpdateG2000SettingsSummary();
    }

    private void UpdateG2000SettingsSummary()
    {
        if (_settings is null)
        {
            return;
        }

        var relay = _settings.Relay;
        var can = relay.G2000Can;
        var mode = relay.ResolveMode();
        var canMode = mode == RelayControllerMode.G2000Can;

        G2000ControlPathText.Text = canMode
            ? string.Format(CultureInfo.InvariantCulture, T("g2000.controlPathCan"), can.Channel, can.NodeId)
            : string.Format(CultureInfo.InvariantCulture, T("g2000.controlPathUnavailable"), mode);
        G2000InterlockText.Text = canMode
            ? relay.DryRun
                ? T("g2000.physicalInterlockDryRun")
                : string.Format(CultureInfo.InvariantCulture, T("g2000.physicalInterlockPort"), relay.PortName)
            : string.Format(CultureInfo.InvariantCulture, T("g2000.relayOutputMode"), mode);
        G2000SetpointsSummaryText.Text = string.Format(
            CultureInfo.InvariantCulture,
            T("g2000.setpointSummary"),
            CompactInput(G2000VoltageBox.Text),
            CompactInput(G2000FrequencyBox.Text),
            CompactInput(G2000DutyBox.Text),
            CompactInput(G2000TonBox.Text),
            CompactInput(G2000ToffBox.Text));
        G2000RecipeSummaryText.Text = string.Format(
            CultureInfo.InvariantCulture,
            T("g2000.recipeSummary"),
            CompactInput(G2000Stage1VoltageBox.Text),
            CompactInput(G2000Stage1DurationBox.Text),
            CompactInput(G2000Stage2VoltageBox.Text),
            FormatRecoveryPolicy((TripRecoveryPolicy?)G2000RecoveryPolicyBox.SelectedValue ?? can.ResolveRecoveryPolicy()),
            G2000Stage2HoldBox.IsChecked == true ? T("g2000.holdStage2") : T("g2000.noHoldStage2"),
            G2000EnterHvReadyBox.IsChecked == true ? T("g2000.readyLead") : T("g2000.noReadyLead"),
            G2000EnterHvOnBox.IsChecked == true ? T("g2000.autoEin") : T("g2000.manualEin"));
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
            _lastTemperatureSampleAt = latest.Timestamp;
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
        _monitoringStartedAt = null;
        SetStatus(_stateMachine.IsTripped ? MonitorStatus.Tripped : MonitorStatus.Idle);
        FooterText.Text = message;
    }

    private void MarkMonitoringSessionStarted()
    {
        _monitoringStartedAt = DateTimeOffset.Now;
        _lastTemperatureC = null;
        _lastTemperatureSampleAt = null;
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
        UpdateG2000SettingsSummary();
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
        ValidateG2000U2SettingsForUi();
        _settings.Relay.G2000Can.ValidateWritableSetpoints(_settings.Relay.G2000Can.WritableSetpoints);
        _settings.Relay.G2000Can.ValidateStartupRecipe(_settings.Relay.G2000Can.StartupRecipe);
        _settings.Amc2100.Normalize();
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        ApplyG2000RecoveryPolicyToController();
        UpdateGasFlowDisplay();
        UpdateG2000SettingsSummary();
    }

    private async Task SaveG2000SettingsFromUiAsync()
    {
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
        _settings.Relay.G2000Can.Normalize();
        ValidateG2000U2SettingsForUi();
        _settings.Relay.G2000Can.ValidateWritableSetpoints(_settings.Relay.G2000Can.WritableSetpoints);
        _settings.Relay.G2000Can.ValidateStartupRecipe(_settings.Relay.G2000Can.StartupRecipe);
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        ApplyG2000RecoveryPolicyToController();
        UpdateG2000SettingsSummary();
    }

    private void ValidateG2000U2SettingsForUi()
    {
        var can = _settings.Relay.G2000Can;
        if (can.AllowUnsafeU2Writes)
        {
            return;
        }

        ValidateG2000U2VoltageForUi(can.WritableSetpoints.VoltageV, T("label.g2000Voltage"), can);
        ValidateG2000U2VoltageForUi(can.StartupRecipe.Stage1VoltageV, T("label.g2000Stage1Voltage"), can);
        ValidateG2000U2VoltageForUi(can.StartupRecipe.Stage2VoltageV, T("label.g2000Stage2Voltage"), can);
    }

    private void ValidateG2000U2VoltageForUi(double value, string name, G2000CanSettings can)
    {
        if (value >= can.VerifiedU2MinVoltageV && value <= can.VerifiedU2MaxVoltageV)
        {
            return;
        }

        throw new InvalidOperationException(string.Format(
            CultureInfo.InvariantCulture,
            T("message.g2000U2OutOfRange"),
            name,
            value,
            can.DescribeVerifiedU2Window()));
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

    private void MonitorLeftScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (MonitorLeftScrollViewer is null || _isRestoringMonitorLeftScroll)
        {
            return;
        }

        if (!ReferenceEquals(MainTabControl.SelectedItem, MonitorTab))
        {
            return;
        }

        _monitorLeftScrollOffset = e.VerticalOffset;
    }

    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MonitorLeftScrollViewer is null || !ReferenceEquals(e.OriginalSource, MainTabControl))
        {
            return;
        }

        if (e.RemovedItems.Contains(MonitorTab))
        {
            _monitorLeftScrollOffset = MonitorLeftScrollViewer.VerticalOffset;
        }

        if (!e.AddedItems.Contains(MonitorTab))
        {
            return;
        }

        var offsetToRestore = _monitorLeftScrollOffset;
        _isRestoringMonitorLeftScroll = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var offset = Math.Min(offsetToRestore, MonitorLeftScrollViewer.ScrollableHeight);
                MonitorLeftScrollViewer.ScrollToVerticalOffset(Math.Max(0, offset));
            }
            finally
            {
                _isRestoringMonitorLeftScroll = false;
            }
        }), DispatcherPriority.Loaded);
    }

    private void G2000SettingsInput_Changed(object sender, RoutedEventArgs e)
    {
        if (_isBindingSettings || _settings is null)
        {
            return;
        }

        UpdateG2000SettingsSummary();
        G2000DiagnosticStatusText.Text = G2000ConnectionText.Text;
    }

    private async void G2000RecoveryPolicyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isBindingSettings || _settings is null)
        {
            return;
        }

        try
        {
            UpdateSelectedG2000RecoveryPolicySetting();
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            ApplyG2000RecoveryPolicyToController();
            UpdateG2000SettingsSummary();
            G2000DiagnosticStatusText.Text = G2000ConnectionText.Text;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void UpdateSelectedG2000RecoveryPolicySetting()
    {
        var policy = (TripRecoveryPolicy?)G2000RecoveryPolicyBox.SelectedValue ?? _settings.Relay.G2000Can.ResolveRecoveryPolicy();
        _settings.Relay.G2000Can.RecoveryPolicy = policy.ToString();
    }

    private void ApplyG2000RecoveryPolicyToController()
    {
        if (_g2000Controller is not null)
        {
            _g2000Controller.RecoveryPolicy = _settings.Relay.G2000Can.ResolveRecoveryPolicy();
        }
    }

    private void ApplyLanguage()
    {
        var wasBindingSettings = _isBindingSettings;
        _isBindingSettings = true;
        RefreshG2000RecoveryPolicyOptions();
        _isBindingSettings = wasBindingSettings;

        Title = T("app.title");
        TitleText.Text = T("app.title");
        VersionText.Text = $"Version {GetSoftwareVersion()}";
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
        G2000Tab.Header = T("tab.g2000");
        EngineeringTab.Header = T("tab.engineering");
        ControlsCardTitle.Text = T("group.controls");
        G2000RunControlTitle.Text = T("group.g2000RunControl");
        G2000HighRiskText.Text = T("g2000.highRisk");
        G2000WritableSetpointsTitle.Text = T("group.g2000WritableSetpoints");
        G2000AutomaticTitle.Text = T("group.g2000Automatic");
        G2000ActualsTitle.Text = T("group.g2000Actuals");
        G2000RawFramesTitle.Text = T("group.g2000RawFrames");
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
        ClearHistoryButton.Content = T("button.clearHistory");
        TestRelayButton.Content = T("button.disconnectAll");
        TestRelayResetButton.Content = T("button.connectAll");
        TripAllEngineeringButton.Content = T("button.disconnectAll");
        RestoreAllEngineeringButton.Content = T("button.connectAll");
        RestoreMonitoringControlButton.Content = T("button.restoreMonitoring");
        SaveSettingsButton.Content = T("button.save");
        G2000RefreshButton.Content = T("button.g2000Refresh");
        G2000HvAusButton.Content = T("button.g2000HvAus");
        G2000HvReadyButton.Content = T("button.g2000HvReady");
        G2000HvEinButton.Content = T("button.g2000HvEin");
        G2000ApplySetpointsButton.Content = T("button.g2000ApplySetpoints");
        G2000StartAutomaticButton.Content = T("button.g2000StartAutomatic");
        G2000StopAutomaticButton.Content = T("button.g2000StopAutomatic");
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
        G2000ConnectionLabel.Text = T("label.g2000Connection");
        G2000SourceLabel.Text = T("label.g2000Source");
        G2000FaultLabel.Text = T("label.g2000Fault");
        G2000TripLabel.Text = T("label.g2000Trip");
        G2000ModeLabel.Text = T("label.g2000Mode");
        G2000StageLabel.Text = T("label.g2000Stage");
        G2000ControlLabel.Text = T("label.g2000Control");
        G2000InterlockLabel.Text = T("label.g2000Interlock");
        G2000SetpointsLabel.Text = T("label.g2000Setpoints");
        G2000RecipeLabel.Text = T("label.g2000Recipe");
        G2000VoltageLabel.Text = T("label.g2000Voltage");
        G2000FrequencyLabel.Text = T("label.g2000Frequency");
        G2000DutyLabel.Text = T("label.g2000Duty");
        G2000TonLabel.Text = T("label.g2000Ton");
        G2000ToffLabel.Text = T("label.g2000Toff");
        G2000Stage1VoltageLabel.Text = T("label.g2000Stage1Voltage");
        G2000Stage1DurationLabel.Text = T("label.g2000Stage1Duration");
        G2000Stage2VoltageLabel.Text = T("label.g2000Stage2Voltage");
        G2000RecoveryPolicyLabel.Text = T("label.g2000Recovery");
        G2000Stage2HoldBox.Content = T("check.g2000HoldStage2");
        G2000EnterHvReadyBox.Content = T("check.g2000EnterHvReady");
        G2000EnterHvOnBox.Content = T("check.g2000EnterHvOn");
        G2000TargetHeaderText.Text = T("label.g2000Target");
        G2000ActualHeaderText.Text = T("label.g2000Actual");
        G2000ActualVoltageLabel.Text = T("label.g2000ActualVoltage");
        G2000ActualDcLinkCurrentLabel.Text = T("label.g2000ActualDcLinkCurrent");
        G2000ActualFrequencyLabel.Text = T("label.g2000ActualFrequency");
        G2000ActualDutyLabel.Text = T("label.g2000ActualDuty");
        G2000ActualTonLabel.Text = T("label.g2000ActualTon");
        G2000ActualToffLabel.Text = T("label.g2000ActualToff");
        G2000ReservedOutputVoltageLabel.Text = T("label.g2000ReservedOutputVoltage");
        G2000ReservedOutputCurrentLabel.Text = T("label.g2000ReservedOutputCurrent");
        G2000U2NoticeText.Text = T("g2000.u2Notice");
        G2000Raw180Label.Text = T("label.g2000Raw180");
        G2000Raw280Label.Text = T("label.g2000Raw280");
        G2000Raw281Label.Text = T("label.g2000Raw281");
        G2000Raw380Label.Text = T("label.g2000Raw380");
        G2000Raw381Label.Text = T("label.g2000Raw381");
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
        ApplyTooltips();
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

    private static string GetSoftwareVersion()
    {
        var informationalVersion = typeof(MainWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return string.IsNullOrWhiteSpace(informationalVersion) ? "dev" : informationalVersion;
    }

    private void ApplyTooltips()
    {
        SetToolTip(LanguageBox, "tooltip.language");
        SetToolTip(PortBox, "tooltip.port");
        SetToolTip(StartButton, "tooltip.startTemperatureMonitor");
        SetToolTip(StopButton, "tooltip.stopTemperatureMonitor");
        SetToolTip(ResetButton, "tooltip.reset");
        SetToolTip(SelectRoiButton, "tooltip.selectRoi");
        SetToolTip(ExportButton, "tooltip.export");
        SetToolTip(ClearHistoryButton, "tooltip.clearHistory");
        SetToolTip(WindowTitleBox, "tooltip.windowTitle");
        SetToolTip(TesseractPathBox, "tooltip.tesseractPath");
        SetToolTip(ThresholdBox, "tooltip.threshold");
        SetToolTip(IntervalBox, "tooltip.interval");
        SetToolTip(RecoveryThresholdBox, "tooltip.recoveryThreshold");
        SetToolTip(StableSecondsBox, "tooltip.stableSeconds");
        SetToolTip(DryRunBox, "tooltip.dryRun");
        SetToolTip(AutoResetBox, "tooltip.autoReset");
        SetToolTip(SaveSettingsButton, "tooltip.saveSettings");
        SetToolTip(G2000RefreshButton, "tooltip.g2000Refresh");
        SetToolTip(G2000HvAusButton, "tooltip.g2000HvAus");
        SetToolTip(G2000HvReadyButton, "tooltip.g2000HvReady");
        SetToolTip(G2000HvEinButton, "tooltip.g2000HvEin");
        SetToolTip(G2000VoltageBox, "tooltip.g2000Voltage");
        SetToolTip(G2000FrequencyBox, "tooltip.g2000Frequency");
        SetToolTip(G2000DutyBox, "tooltip.g2000Duty");
        SetToolTip(G2000TonBox, "tooltip.g2000Ton");
        SetToolTip(G2000ToffBox, "tooltip.g2000Toff");
        SetToolTip(G2000ApplySetpointsButton, "tooltip.g2000ApplySetpoints");
        SetToolTip(G2000Stage1VoltageBox, "tooltip.g2000Stage1Voltage");
        SetToolTip(G2000Stage1DurationBox, "tooltip.g2000Stage1Duration");
        SetToolTip(G2000Stage2VoltageBox, "tooltip.g2000Stage2Voltage");
        SetToolTip(G2000RecoveryPolicyBox, "tooltip.g2000RecoveryPolicy");
        SetToolTip(G2000Stage2HoldBox, "tooltip.g2000HoldStage2");
        SetToolTip(G2000EnterHvReadyBox, "tooltip.g2000EnterHvReady");
        SetToolTip(G2000EnterHvOnBox, "tooltip.g2000EnterHvOn");
        SetToolTip(G2000StartAutomaticButton, "tooltip.g2000StartAutomatic");
        SetToolTip(G2000StopAutomaticButton, "tooltip.g2000StopAutomatic");
        SetToolTip(AmcEnabledBox, "tooltip.amcEnabled");
        SetToolTip(AmcPortBox, "tooltip.amcPort");
        SetToolTip(AmcBaudBox, "tooltip.amcBaud");
        SetToolTip(AmcSlaveBox, "tooltip.amcSlave");
        SetToolTip(AmcFallbackBox, "tooltip.amcFallback");
        SetToolTip(AmcForceDigitalModeBox, "tooltip.amcDigitalMode");
        SetToolTip(StopGasButton, "tooltip.stopGas");
        SetToolTip(RestoreGasButton, "tooltip.restoreGas");
        SetToolTip(RestoreMonitoringControlButton, "tooltip.restoreMonitoringControl");
        SetToolTip(G2000Raw180Text, "tooltip.g2000Raw180");
        SetToolTip(G2000Raw280Text, "tooltip.g2000Raw280");
        SetToolTip(G2000Raw281Text, "tooltip.g2000Raw281");
        SetToolTip(G2000Raw380Text, "tooltip.g2000Raw380");
        SetToolTip(G2000Raw381Text, "tooltip.g2000Raw381");
        SetToolTip(FileMenu, "tooltip.menuFile");
        SetToolTip(ToolsMenu, "tooltip.menuTools");
        SetToolTip(HelpMenu, "tooltip.menuHelp");
        SetToolTip(ExportMenuItem, "tooltip.export");
        SetToolTip(SelectRoiMenuItem, "tooltip.selectRoi");
        SetToolTip(GuideMenuItem, "tooltip.openGuide");
        SetToolTip(AdvancedSettingsMenuItem, "tooltip.advancedSettings");
        SetToolTip(WiringNotesMenuItem, "tooltip.openGuide");
        SetToolTip(AboutMenuItem, "tooltip.about");
        SetToolTip(ExitMenuItem, "tooltip.exit");

        foreach (var button in FindVisualChildren<Button>(this))
        {
            if (button.Tag is not string tag)
            {
                continue;
            }

            var parts = tag.Split(':', 2);
            if (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
            {
                continue;
            }

            button.ToolTip = string.Format(
                CultureInfo.InvariantCulture,
                delta > 0 ? T("tooltip.g2000StepIncrease") : T("tooltip.g2000StepDecrease"),
                DescribeG2000SetpointForTooltip(parts[0]));
        }
    }

    private void SetToolTip(FrameworkElement element, string key)
    {
        element.ToolTip = T(key);
    }

    private string DescribeG2000SetpointForTooltip(string name)
    {
        return name switch
        {
            "VoltageV" => T("label.g2000Voltage"),
            "FrequencyKhz" => T("label.g2000Frequency"),
            "DutyPercent" => T("label.g2000Duty"),
            "TonMs" => T("label.g2000Ton"),
            "ToffMs" => T("label.g2000Toff"),
            _ => name
        };
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
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
            var physicalFallbackText = _settings.Relay.DryRun
                ? T("relayBank.g2000PhysicalFallbackDryRun")
                : string.Format(CultureInfo.InvariantCulture, T("relayBank.g2000PhysicalFallbackPort"), _settings.Relay.PortName);
            RelayBankStatusText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "CAN {0} | {1} | {2} | {3}",
                FormatG2000ControlSource(_lastG2000Snapshot.Source),
                DescribeG2000HvState(_lastG2000Snapshot),
                FormatG2000Fault(_lastG2000Snapshot),
                physicalFallbackText);
            foreach (var channel in _channelUis)
            {
                channel.StateText.Text = _settings.Relay.DryRun ? T("relayBank.g2000ChannelDryRun") : T("relayBank.g2000ChannelPhysical");
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
        var relayMode = _settings?.Relay.ResolveMode();
        var isCanMode = relayMode == RelayControllerMode.G2000Can;
        G2000Tab.IsEnabled = true;
        G2000Tab.ToolTip = isCanMode
            ? null
            : string.Format(CultureInfo.InvariantCulture, T("tooltip.g2000TabReadOnly"), _settings?.Relay.Mode ?? "--");
        var physicalRelayAvailable = isCanMode && _settings is not null && !_settings.Relay.DryRun;

        TestRelayButton.IsEnabled = true;
        TestRelayResetButton.IsEnabled = true;
        TripAllEngineeringButton.IsEnabled = true;
        RestoreAllEngineeringButton.IsEnabled = true;

        var branchInterlockTooltip = BuildBranchInterlockTooltip(relayMode, _settings?.Relay.DryRun == true);
        TestRelayButton.ToolTip = branchInterlockTooltip;
        TestRelayResetButton.ToolTip = branchInterlockTooltip;
        TripAllEngineeringButton.ToolTip = branchInterlockTooltip;
        RestoreAllEngineeringButton.ToolTip = branchInterlockTooltip;

        foreach (var channel in _channelUis)
        {
            channel.OpenButton.IsEnabled = true;
            channel.CloseButton.IsEnabled = true;
            channel.OpenButton.ToolTip = branchInterlockTooltip;
            channel.CloseButton.ToolTip = branchInterlockTooltip;
        }

        if (isCanMode)
        {
            EngineeringNotesText.Text = physicalRelayAvailable
                ? T("engineering.g2000CanPhysicalNotes")
                : T("engineering.g2000CanDryRunNotes");
            TestRelayButton.Content = T("button.openPhysicalInterlock");
            TestRelayResetButton.Content = T("button.closePhysicalInterlock");
            TripAllEngineeringButton.Content = T("button.openPhysicalInterlock");
            RestoreAllEngineeringButton.Content = T("button.closePhysicalInterlock");
        }
        else
        {
            EngineeringNotesText.Text = T("engineering.notes");
            TestRelayButton.Content = T("button.disconnectAll");
            TestRelayResetButton.Content = T("button.connectAll");
            TripAllEngineeringButton.Content = T("button.disconnectAll");
            RestoreAllEngineeringButton.Content = T("button.connectAll");
        }

        UpdateG2000SettingsSummary();
        G2000DiagnosticStatusText.Text = G2000ConnectionText.Text;
    }

    private string FormatBranchInterlockFooter(string footerText)
    {
        return _settings.Relay.ResolveMode() == RelayControllerMode.DryRun
            ? string.Format(CultureInfo.InvariantCulture, T("footer.noHardwareDryRun"), footerText)
            : footerText;
    }

    private string BuildBranchInterlockTooltip(RelayControllerMode? relayMode, bool dryRun)
    {
        if (relayMode == RelayControllerMode.DryRun)
        {
            return T("tooltip.branchInterlockDryRun");
        }

        if (relayMode == RelayControllerMode.G2000Can && dryRun)
        {
            return T("tooltip.branchInterlockCanDryRun");
        }

        if (relayMode == RelayControllerMode.G2000Can)
        {
            return T("tooltip.branchInterlockPhysical");
        }

        return T("tooltip.branchInterlockDefault");
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

    private void StartMonitoringLoop(string footerText)
    {
        _monitoringCts = new CancellationTokenSource();
        MarkMonitoringSessionStarted();
        SetStatus(MonitorStatus.Monitoring);
        SetCurrentMode(T("mode.monitoring"));
        FooterText.Text = footerText;
        _ = Task.Run(() => RunMonitoringLoopAsync(_monitoringCts.Token));
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
        MarkMonitoringSessionStarted();
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

    private string DescribeG2000HvState(G2000TelemetrySnapshot snapshot)
    {
        return snapshot.HvOn
            ? T("g2000.hvEin")
            : snapshot.HvEnable
                ? T("g2000.hvReady")
                : T("g2000.hvAus");
    }

    private string FormatG2000ControlSource(G2000ControlSource source)
    {
        return source switch
        {
            G2000ControlSource.Internal => T("g2000.sourceInternal"),
            G2000ControlSource.External => T("g2000.sourceExternal"),
            G2000ControlSource.CanBus => T("g2000.sourceCanBus"),
            _ => T("g2000.sourceFrontPanelOrUnknown")
        };
    }

    private string FormatG2000Fault(G2000TelemetrySnapshot snapshot)
    {
        if (!snapshot.Fault)
        {
            return T("g2000.noFault");
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} (0x{1:X2})",
            FormatG2000ErrorCode(snapshot.ErrorCode),
            snapshot.ErrorCode);
    }

    private string FormatG2000ErrorCode(byte errorCode)
    {
        return errorCode switch
        {
            0x07 => T("g2000.errorInternalCanTimeout"),
            0x08 => T("g2000.errorInterlock"),
            0x09 => T("g2000.errorExternalCanTimeout"),
            _ => string.Format(CultureInfo.InvariantCulture, T("g2000.errorUnknown"), errorCode)
        };
    }

    private string FormatG2000TripReason(string reason)
    {
        return reason switch
        {
            "Temperature limit trip" => T("g2000.tripTemperatureLimit"),
            "Internal CAN timeout" => T("g2000.errorInternalCanTimeout"),
            "Interlock fault" => T("g2000.errorInterlock"),
            "External CAN timeout" => T("g2000.errorExternalCanTimeout"),
            _ when string.IsNullOrWhiteSpace(reason) => T("g2000.tripLatched"),
            _ => reason
        };
    }

    private static string FormatNullableDouble(double? value, string format)
    {
        return value?.ToString(format, CultureInfo.InvariantCulture) ?? "--";
    }

    private string FormatG2000UiMode(G2000UiMode mode)
    {
        return mode == G2000UiMode.Automatic ? T("g2000.modeAutomatic") : T("g2000.modeManual");
    }

    private string FormatG2000Stage(string stage)
    {
        return stage switch
        {
            "Idle" => T("g2000.stageIdle"),
            "ReadyLead" => T("g2000.stageReadyLead"),
            "Stage1" => T("g2000.stage1"),
            "Stage2" => T("g2000.stage2"),
            "Manual" => T("g2000.stageManual"),
            "PreparedRecovery" => T("g2000.stagePreparedRecovery"),
            "Tripped" => T("g2000.stageTripped"),
            "Stopped" => T("g2000.stageStopped"),
            "RecoveredToHvReady" => T("g2000.stageRecoveredToHvReady"),
            "HoldHvAus" => T("g2000.stageHoldHvAus"),
            _ => stage
        };
    }

    private string FormatRecoveryPolicy(TripRecoveryPolicy policy)
    {
        return policy switch
        {
            TripRecoveryPolicy.HoldHvAus => T("g2000.recoveryHoldHvAus"),
            TripRecoveryPolicy.RestoreHvReady => T("g2000.recoveryRestoreHvReady"),
            TripRecoveryPolicy.RestorePreviousState => T("g2000.recoveryRestorePreviousState"),
            _ => policy.ToString()
        };
    }

    private static string CompactInput(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();
    }

    private static bool IsPcanConnectionError(Exception ex)
    {
        return ex.Message.Contains("PCAN", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ApplyG2000WritableSetpointsFromUiAsync(IG2000Controller controller, string footerText)
    {
        await SaveG2000SettingsFromUiAsync();
        await controller.ApplyWritableSetpointsAsync(_settings.Relay.G2000Can.WritableSetpoints.Clone(), CancellationToken.None);
        FooterText.Text = footerText;
    }

    private TextBox GetG2000WritableSetpointBox(string name, out string format)
    {
        switch (name)
        {
            case "VoltageV":
                format = "0.0";
                return G2000VoltageBox;
            case "FrequencyKhz":
                format = "0.0";
                return G2000FrequencyBox;
            case "DutyPercent":
                format = "0.0";
                return G2000DutyBox;
            case "TonMs":
                format = "0.000";
                return G2000TonBox;
            case "ToffMs":
                format = "0.000";
                return G2000ToffBox;
            default:
                throw new InvalidOperationException($"Unsupported G2000 setpoint '{name}'.");
        }
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

        ShowSetupWarning(T("message.g2000PendingSetpoints"));
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
