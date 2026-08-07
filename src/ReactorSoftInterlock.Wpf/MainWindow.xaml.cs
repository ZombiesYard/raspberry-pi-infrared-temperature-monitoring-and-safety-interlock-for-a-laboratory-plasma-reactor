using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Net.Http;
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
    private ExperimentEvidenceCoordinator? _experimentRecorder;
    private string? _experimentRecorderInitializationError;
    private Task<ExperimentEvidenceCoordinator>? _experimentRecorderCreationTask;
    private CancellationTokenSource? _experimentRecorderInitializationCts;
    private readonly IExperimentCredentialStore _experimentCredentialStore = new WindowsExperimentCredentialStore();
    private HttpClient? _experimentUploadHttpClient;
    private InterlockStateMachine _stateMachine = null!;
    private MonitoringService? _monitoringService;
    private IRelayBankController? _relayBankController;
    private IG2000Controller? _g2000Controller;
    private CancellationTokenSource? _monitoringCts;
    private Task? _monitoringTask;
    private readonly DispatcherTimer _gasFlowTimer = new();
    private double? _lastTemperatureC;
    private DateTimeOffset? _lastTemperatureSampleAt;
    private DateTimeOffset? _monitoringStartedAt;
    private double? _lastGasFlowMlMin;
    private MonitorStatus _currentStatus = MonitorStatus.Idle;
    private G2000TelemetrySnapshot _lastG2000Snapshot = new();
    private readonly object _g2000TelemetryUiSync = new();
    private G2000TelemetrySnapshot? _pendingG2000TelemetryUiSnapshot;
    private bool _g2000TelemetryUiUpdateScheduled;
    private string? _lastG2000ConnectionError;
    private string? _relayRuntimeKey;
    private readonly SemaphoreSlim _branchInterlockGate = new(1, 1);
    private readonly SemaphoreSlim _gasFlowGate = new(1, 1);
    private readonly SemaphoreSlim _gasFlowActionGate = new(1, 1);
    private readonly SemaphoreSlim _monitoringLifecycleGate = new(1, 1);
    private readonly CancellationTokenSource _gasFlowLifetimeCts = new();
    private bool _isBindingSettings;
    private Task<double?>? _gasFlowReadTask;
    private bool _isGasFlowActionBusy;
    private double _monitorLeftScrollOffset;
    private bool _isRestoringMonitorLeftScroll;
    private bool _shutdownPreparing;
    private bool _shutdownReady;
    private bool _isRunFinalizing;
    private bool _initializationComplete;

    public MainWindow()
    {
        InitializeComponent();
        IsEnabled = false;
        HistoryGrid.ItemsSource = _history;
        InitializeChannelUi();
        _gasFlowTimer.Interval = TimeSpan.FromSeconds(1);
        _gasFlowTimer.Tick += GasFlowTimer_Tick;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _settingsStore = new SettingsStore(_settingsPath);
            _settings = await _settingsStore.LoadAsync(CancellationToken.None);
            if (_shutdownPreparing)
            {
                return;
            }

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
            await InitializeExperimentRecorderAsync();
            if (_shutdownPreparing)
            {
                return;
            }

            await InitializeG2000ControllerAsync();
            if (_shutdownPreparing)
            {
                return;
            }

            SetAllChannelStates(null);
            SetCurrentMode(T("mode.monitoring"));
            UpdateGasFlowDisplay();
            _gasFlowTimer.Start();
            await LoadRecentHistoryAsync();
            if (_shutdownPreparing)
            {
                return;
            }

            DrawTemperatureChart();
            _initializationComplete = true;
            _experimentRecorderCreationTask = null;
            _experimentRecorderInitializationCts?.Dispose();
            _experimentRecorderInitializationCts = null;
            IsEnabled = true;
            UpdateRunBoundaryButtons();
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
        _gasFlowLifetimeCts.Cancel();
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
        if (_g2000Controller is not null)
        {
            _g2000Controller.TelemetryUpdated -= G2000Controller_TelemetryUpdated;
        }

        (_relayBankController as IDisposable)?.Dispose();

        try { _experimentRecorder?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _experimentRecorder = null;
        _experimentUploadHttpClient?.Dispose();
        _experimentUploadHttpClient = null;
        _gasFlowLifetimeCts.Dispose();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownReady)
        {
            return;
        }

        _gasFlowLifetimeCts.Cancel();
        if (!_initializationComplete)
        {
            e.Cancel = true;
            if (_shutdownPreparing)
            {
                return;
            }

            _shutdownPreparing = true;
            IsEnabled = false;
            _experimentRecorderInitializationCts?.Cancel();
            var creationTask = _experimentRecorderCreationTask;
            var startupCoordinator = _experimentRecorder;
            using var initializationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (startupCoordinator is null && creationTask is not null)
            {
                try
                {
                    startupCoordinator = await creationTask.WaitAsync(initializationTimeout.Token);
                }
                catch
                {
                    _ = DisposeLateExperimentRecorderAsync(creationTask);
                }
            }

            if (startupCoordinator is not null)
            {
                try
                {
                    await startupCoordinator.DisposeAsync().AsTask().WaitAsync(initializationTimeout.Token);
                }
                catch
                {
                    // Startup recovery owns any session that cannot close within the limit.
                }
            }

            _experimentRecorder = null;
            _experimentRecorderInitializationCts?.Dispose();
            _experimentRecorderInitializationCts = null;
            _shutdownReady = true;
            _ = Dispatcher.BeginInvoke(Close);
            return;
        }
        if (_settings is null || _experimentRecorder is null)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownPreparing)
        {
            return;
        }

        _shutdownPreparing = true;
        IsEnabled = false;
        _gasFlowTimer.Stop();
        var monitoringTask = StopMonitoring(T("footer.monitoringStopped"));
        var coordinator = _experimentRecorder;
        coordinator.RecordEvent("application", "session-stopped", "success");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var lifecycleAcquired = false;
        var gasActionAcquired = false;
        var gasIoAcquired = false;
        try
        {
            await AwaitMonitoringTaskAsync(monitoringTask, timeout.Token);
            await _monitoringLifecycleGate.WaitAsync(timeout.Token);
            lifecycleAcquired = true;
            await _gasFlowActionGate.WaitAsync(timeout.Token);
            gasActionAcquired = true;
            await _gasFlowGate.WaitAsync(timeout.Token);
            gasIoAcquired = true;
            await coordinator.FinalizeRunAsync(
                    _settings,
                    "application-exit",
                    startNextRun: false,
                    timeout.Token)
                .WaitAsync(timeout.Token);
        }
        catch
        {
            // A dirty run remains recoverable from its session directory.
        }

        try { await coordinator.DisposeAsync().AsTask().WaitAsync(timeout.Token); } catch { }
        if (lifecycleAcquired)
        {
            _monitoringLifecycleGate.Release();
        }
        if (gasIoAcquired)
        {
            _gasFlowGate.Release();
        }
        if (gasActionAcquired)
        {
            _gasFlowActionGate.Release();
        }
        _experimentRecorder = null;
        _shutdownReady = true;
        _ = Dispatcher.BeginInvoke(Close);
    }

    private async Task InitializeExperimentRecorderAsync()
    {
        try
        {
            var dataDirectory = ResolveDataDirectory();
            _experimentUploadHttpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            {
                Timeout = TimeSpan.FromSeconds(_settings.ExperimentUpload.HttpTimeoutSeconds)
            };
            var uploader = new GitLabExperimentUploader(
                _experimentUploadHttpClient,
                _experimentCredentialStore);
            _experimentRecorderInitializationCts = new CancellationTokenSource();
            var creationTask = Task.Run(() => ExperimentEvidenceCoordinator.CreateAsync(
                dataDirectory,
                _settings,
                GetSoftwareVersion(),
                uploader,
                _experimentRecorderInitializationCts.Token));
            _experimentRecorderCreationTask = creationTask;
            var coordinator = await creationTask;
            if (_shutdownPreparing)
            {
                return;
            }

            _experimentRecorder = coordinator;
            _experimentRecorder.UploadStateChanged += ExperimentRecorder_UploadStateChanged;
            var credentialAvailable = false;
            try
            {
                var credential = await _experimentCredentialStore.ReadTokenAsync(
                    _settings.ExperimentUpload.CredentialTarget,
                    CancellationToken.None);
                credentialAvailable = !string.IsNullOrWhiteSpace(credential);
            }
            catch
            {
                // Credential failures disable upload but must not disable local evidence recording.
            }
            _experimentRecorder.NotifyCredentialAvailability(credentialAvailable);
            UpdateExperimentUploadStatus(_experimentRecorder.UploadState);
            _experimentRecorderInitializationError = null;
        }
        catch (Exception ex)
        {
            _experimentRecorder = null;
            _experimentRecorderInitializationError = ex.Message;
            _experimentUploadHttpClient?.Dispose();
            _experimentUploadHttpClient = null;
            if (!_shutdownPreparing)
            {
                throw new InvalidOperationException(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        T("message.experimentRecorderUnavailable"),
                        ex.Message),
                    ex);
            }
        }
    }

    private static async Task DisposeLateExperimentRecorderAsync(
        Task<ExperimentEvidenceCoordinator> creationTask)
    {
        try
        {
            var recorder = await creationTask.ConfigureAwait(false);
            await recorder.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // A cancelled or failed startup task has no coordinator to dispose.
        }
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

    private G2000ConnectionAssessmentKind AssessCurrentG2000Connection()
    {
        var snapshot = _g2000Controller?.Snapshot ?? _lastG2000Snapshot;
        return G2000ConnectionAssessment.Assess(snapshot, _lastG2000ConnectionError);
    }

    private async Task<G2000ConnectionAssessmentKind> WaitForG2000CommunicationAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        G2000ConnectionAssessmentKind assessment;
        do
        {
            assessment = AssessCurrentG2000Connection();
            if (assessment == G2000ConnectionAssessmentKind.Confirmed ||
                assessment == G2000ConnectionAssessmentKind.PcanUnavailable ||
                DateTimeOffset.UtcNow >= deadline)
            {
                return assessment;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }
        while (!_shutdownPreparing);

        return assessment;
    }

    private string DescribeG2000ConnectionProblem(G2000ConnectionAssessmentKind assessment)
    {
        return assessment switch
        {
            G2000ConnectionAssessmentKind.PcanUnavailable =>
                string.IsNullOrWhiteSpace(_lastG2000ConnectionError)
                    ? T("message.g2000PcanUnavailableDetail")
                    : _lastG2000ConnectionError,
            G2000ConnectionAssessmentKind.NoRecentTelemetry => T("message.g2000TelemetryMissingDetail"),
            _ => string.Empty
        };
    }

    private async void SelectRoiButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMonitoringLifecycle())
        {
            return;
        }

        try
        {
            if (!await SaveSettingsFromUiAsync())
            {
                return;
            }
            if (_shutdownPreparing)
            {
                return;
            }

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
            var candidateSettings = _settings.Clone();
            candidateSettings.Roi.X = (int)Math.Max(0, selected.X - windowBounds.Left);
            candidateSettings.Roi.Y = (int)Math.Max(0, selected.Y - windowBounds.Top);
            candidateSettings.Roi.Width = (int)Math.Max(1, selected.Width);
            candidateSettings.Roi.Height = (int)Math.Max(1, selected.Height);
            await ApplySettingsCandidateAtRuntimeBoundaryAsync(candidateSettings);
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            if (_shutdownPreparing)
            {
                return;
            }

            _experimentRecorder?.UpdateSettings(_settings);
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
        finally
        {
            EndMonitoringLifecycle();
        }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_monitoringCts is not null || !TryBeginMonitoringLifecycle())
        {
            return;
        }
        try
        {
            if (!await SaveSettingsFromUiAsync())
            {
                return;
            }
            if (_shutdownPreparing)
            {
                return;
            }

            if (!ValidateMonitoringSetup())
            {
                return;
            }

            await RebuildServicesPreservingG2000Async();
            if (_shutdownPreparing)
            {
                return;
            }

            await FinalizeExperimentRunAfterStopAsync("monitoring-start-boundary");
            if (_shutdownPreparing)
            {
                return;
            }

            StartMonitoringLoop(T("footer.monitoringStarted"));
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            EndMonitoringLifecycle();
        }
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMonitoringLifecycle())
        {
            return;
        }
        try
        {
            var monitoringTask = StopMonitoring(T("footer.monitoringStopped"));
            if (monitoringTask is not null)
            {
                await AwaitMonitoringTaskAsync(monitoringTask, CancellationToken.None);
                await FinalizeExperimentRunAfterStopAsync("monitoring-stopped");
            }
        }
        finally
        {
            EndMonitoringLifecycle();
        }
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMonitoringLifecycle())
        {
            return;
        }

        try
        {
            if (_monitoringService is null)
            {
                throw new InvalidOperationException(T("message.monitoringServiceUnavailable"));
            }

            var relayAction = await _monitoringService.TryManualResetAsync(_lastTemperatureC, CancellationToken.None);
            if (relayAction is null)
            {
                _experimentRecorder?.RecordEvent(
                    "interlock",
                    "manual-reset",
                    "blocked",
                    $"temperature_c={FormatDoubleForEvidence(_lastTemperatureC)}");
                MessageBox.Show(this, T("message.resetBlocked"), T("message.resetBlockedTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetStatus(MonitorStatus.Monitoring);
            SetAllChannelStates(true);
            AlarmReasonText.Text = string.Empty;
            FooterText.Text = $"{T("footer.resetComplete")}: {relayAction}.";
            _experimentRecorder?.RecordEvent(
                "interlock",
                "manual-reset",
                "command-completed",
                $"relay_action={relayAction}; temperature_c={FormatDoubleForEvidence(_lastTemperatureC)}; hardware_feedback=false");
        }
        catch (Exception ex)
        {
            _experimentRecorder?.RecordEvent("interlock", "manual-reset", "failed", ex.Message);
            ShowError(ex);
        }
        finally
        {
            EndMonitoringLifecycle();
        }
    }

    private async void TestRelayButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMonitoringLifecycle())
        {
            return;
        }

        try
        {
            if (!ValidateBranchInterlockSetup(requireRestore: false))
            {
                _experimentRecorder?.RecordEvent(
                    "relay",
                    "open-all-interlocks",
                    "blocked",
                    "Engineering validation did not pass.");
                return;
            }

            await ExecuteBranchInterlockCommandAsync(static controller => controller.OpenAllInterlocksAsync(CancellationToken.None));
            SetAllChannelStates(false);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = FormatBranchInterlockFooter(T("footer.allDisconnected"));
            _experimentRecorder?.RecordEvent(
                "relay",
                "open-all-interlocks",
                "command-completed",
                "source=engineering-test; hardware_feedback=false");
        }
        catch (Exception ex)
        {
            _experimentRecorder?.RecordEvent("relay", "open-all-interlocks", "failed", ex.Message);
            ShowError(ex);
        }
        finally
        {
            EndMonitoringLifecycle();
        }
    }

    private async void TestRelayResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMonitoringLifecycle())
        {
            return;
        }

        try
        {
            if (!await SaveSettingsFromUiAsync())
            {
                return;
            }
            if (!ValidateBranchInterlockSetup(requireRestore: true))
            {
                _experimentRecorder?.RecordEvent(
                    "relay",
                    "close-all-interlocks",
                    "blocked",
                    "Engineering validation did not pass.");
                return;
            }

            await ExecuteBranchInterlockCommandAsync(static controller => controller.CloseAllInterlocksAsync(CancellationToken.None));
            SetAllChannelStates(true);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = FormatBranchInterlockFooter(T("footer.allConnected"));
            _experimentRecorder?.RecordEvent(
                "relay",
                "close-all-interlocks",
                "command-completed",
                "source=engineering-test; hardware_feedback=false");
        }
        catch (Exception ex)
        {
            _experimentRecorder?.RecordEvent("relay", "close-all-interlocks", "failed", ex.Message);
            ShowError(ex);
        }
        finally
        {
            EndMonitoringLifecycle();
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
        if (!TryBeginMonitoringLifecycle())
        {
            return;
        }

        try
        {
            if (closed && !await SaveSettingsFromUiAsync())
            {
                return;
            }

            if (sender is not FrameworkElement { Tag: string tag } || !int.TryParse(tag, out var channelNumber))
            {
                return;
            }

            if (!ValidateBranchInterlockSetup(requireRestore: true, specificChannelNumber: channelNumber))
            {
                _experimentRecorder?.RecordEvent(
                    "relay",
                    closed ? "close-channel" : "open-channel",
                    "blocked",
                    $"channel={channelNumber}; engineering validation did not pass");
                return;
            }

            await ExecuteBranchInterlockCommandAsync(controller => controller.SetChannelClosedAsync(channelNumber, closed, CancellationToken.None));
            SetChannelState(channelNumber, closed);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = FormatBranchInterlockFooter(string.Format(CultureInfo.InvariantCulture, footerTemplate, channelNumber));
            _experimentRecorder?.RecordEvent(
                "relay",
                closed ? "close-channel" : "open-channel",
                "command-completed",
                $"channel={channelNumber}; hardware_feedback=false");
        }
        catch (Exception ex)
        {
            _experimentRecorder?.RecordEvent(
                "relay",
                closed ? "close-channel" : "open-channel",
                "failed",
                ex.Message);
            ShowError(ex);
        }
        finally
        {
            EndMonitoringLifecycle();
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

    private async void ExportExperimentButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMonitoringLifecycle())
        {
            return;
        }

        try
        {
            if (_experimentRecorder is null)
            {
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    T("message.experimentRecorderUnavailable"),
                    _experimentRecorderInitializationError ?? string.Empty));
            }

            var dialog = new SaveFileDialog
            {
                Filter = T("dialog.experimentZipFilter"),
                FileName = _experimentRecorder.SuggestedBundleFileName
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            _experimentRecorder.RecordEvent(
                "evidence",
                "bundle-export-requested",
                "success",
                Path.GetFileName(dialog.FileName));
            await _experimentRecorder.ExportAsync(dialog.FileName, CancellationToken.None);
            _experimentRecorder.RecordEvent(
                "evidence",
                "bundle-exported",
                "success",
                Path.GetFileName(dialog.FileName));
            FooterText.Text = $"{T("footer.experimentExported")}: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            EndMonitoringLifecycle();
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
        if (!TryBeginMonitoringLifecycle())
        {
            return;
        }

        if (!TryBeginGasFlowAction())
        {
            EndMonitoringLifecycle();
            return;
        }

        try
        {
            var applyErrors = new List<string>();
            var candidateSettings = BuildSettingsCandidateFromUi();
            var monitoringWasRunning = _monitoringCts is not null;
            if (monitoringWasRunning && !CanApplyCandidateDuringMonitoring(candidateSettings, showWarning: true))
            {
                return;
            }

            if (monitoringWasRunning && !ValidateMonitoringSetup(candidateSettings))
            {
                _experimentRecorder?.RecordEvent(
                    "settings",
                    "validate-before-live-apply",
                    "blocked",
                    "The candidate setup did not pass validation; the existing monitoring loop and settings remain active.");
                return;
            }

            var monitoringApplied = false;
            if (monitoringWasRunning)
            {
                if (_monitoringService is null)
                {
                    throw new InvalidOperationException(T("message.monitoringServiceUnavailable"));
                }

                try
                {
                    await ApplySettingsCandidateAtRuntimeBoundaryAsync(candidateSettings);
                    monitoringApplied = true;
                    _experimentRecorder?.RecordEvent(
                        "settings",
                        "apply-monitoring-live",
                        "success",
                        "monitoring_task_restarted=false; output_controller_rebuilt=false; g2000_reconnected=false");
                }
                catch (Exception ex)
                {
                    _experimentRecorder?.RecordEvent("settings", "apply-monitoring-live", "failed", ex.Message);
                    ShowError(new InvalidOperationException(string.Format(
                        CultureInfo.InvariantCulture,
                        T("message.settingsMonitoringApplyFailed"),
                        ex.Message), ex));
                    return;
                }
            }
            else
            {
                Volatile.Write(ref _settings, candidateSettings);
                try
                {
                    monitoringApplied = await RebuildOrRestartMonitoringAsync(ensureG2000Connected: false);
                }
                catch (Exception ex)
                {
                    applyErrors.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        T("message.settingsMonitoringApplyFailed"),
                        ex.Message));
                    _experimentRecorder?.RecordEvent("settings", "apply-monitoring", "failed", ex.Message);
                }
            }

            var settingsPersisted = true;
            try
            {
                await PersistCurrentSettingsAsync();
            }
            catch (Exception ex)
            {
                settingsPersisted = false;
                applyErrors.Add(string.Format(
                    CultureInfo.InvariantCulture,
                    T("message.settingsPersistFailed"),
                    ex.Message));
                _experimentRecorder?.RecordEvent("settings", "persist", "failed", ex.Message);
            }

            ApplyG2000RecoveryPolicyToController();
            UpdateGasFlowDisplay();
            UpdateG2000SettingsSummary();

            if (_shutdownPreparing)
            {
                return;
            }

            var gasFlowApplied = !_settings.Amc2100.Enabled;
            if (_settings.Amc2100.Enabled)
            {
                if (!ValidateAmc2100Setup(requireEnabled: true))
                {
                    _experimentRecorder?.RecordEvent(
                        "amc2100",
                        "set-flow-target",
                        "blocked",
                        "AMC2100 validation did not pass while saving settings.");
                }
                else
                {
                    try
                    {
                        await ApplyConfiguredGasFlowAsync("set-flow-target");
                        gasFlowApplied = true;
                    }
                    catch (OperationCanceledException) when (_gasFlowLifetimeCts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        applyErrors.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            T("message.settingsGasApplyFailed"),
                            ex.Message));
                        _experimentRecorder?.RecordEvent("amc2100", "set-flow-target", "failed", ex.Message);
                    }
                }
            }

            if (_shutdownPreparing)
            {
                return;
            }

            var g2000Required = _settings.Relay.ResolveMode() == RelayControllerMode.G2000Can;
            var g2000Applied = !g2000Required;
            if (g2000Required)
            {
                try
                {
                    G2000ConnectionAssessmentKind connectionAssessment;
                    var actionName = monitoringWasRunning
                        ? "check-after-live-settings"
                        : "initialize-after-settings";
                    if (monitoringWasRunning)
                    {
                        connectionAssessment = AssessCurrentG2000Connection();
                    }
                    else
                    {
                        await InitializeG2000ControllerAsync();
                        connectionAssessment = await WaitForG2000CommunicationAsync(TimeSpan.FromSeconds(2));
                    }

                    if (connectionAssessment == G2000ConnectionAssessmentKind.Confirmed)
                    {
                        g2000Applied = true;
                    }
                    else
                    {
                        var technicalDetail = DescribeG2000ConnectionProblem(connectionAssessment);
                        applyErrors.Add(string.Format(
                            CultureInfo.InvariantCulture,
                            T("message.settingsG2000InitializeFailed"),
                            technicalDetail));
                        _experimentRecorder?.RecordEvent(
                            "g2000",
                            actionName,
                            "warning",
                            monitoringWasRunning
                                ? $"No reconnect was attempted while monitoring was running. {technicalDetail}"
                                : technicalDetail);
                    }
                }
                catch (Exception ex)
                {
                    applyErrors.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        T("message.settingsG2000InitializeFailed"),
                        ex.Message));
                    _experimentRecorder?.RecordEvent("g2000", "initialize-after-settings", "failed", ex.Message);
                }
            }

            if (applyErrors.Count > 0)
            {
                ShowError(new InvalidOperationException(string.Join(Environment.NewLine, applyErrors)));
            }

            var footerParts = new List<string>
            {
                settingsPersisted
                    ? $"{T("footer.settingsSaved")}: {_settingsPath}."
                    : T("footer.settingsAppliedNotPersisted"),
                monitoringApplied
                    ? monitoringWasRunning
                        ? T("footer.settingsMonitoringLiveAppliedSuffix")
                        : T("footer.settingsMonitoringAppliedSuffix")
                    : T("footer.settingsMonitoringNotAppliedSuffix")
            };

            if (_stateMachine.IsTripped)
            {
                footerParts.Add(_monitoringCts is null
                    ? T("footer.tripLatchedStoppedSuffix")
                    : _settings.AutoResetEnabled
                        ? string.Format(CultureInfo.InvariantCulture, T("footer.tripLatchedAutoSuffix"), _settings.RecoveryThresholdC, _settings.RecoveryStableSeconds)
                        : T("footer.tripLatchedManualSuffix"));
            }

            if (_settings.Amc2100.Enabled)
            {
                footerParts.Add(gasFlowApplied
                    ? string.Format(CultureInfo.InvariantCulture, T("footer.settingsGasAppliedSuffix"), _settings.Amc2100.FallbackRestoreSetpointMlMin)
                    : T("footer.settingsGasNotAppliedSuffix"));
            }

            if (g2000Required)
            {
                footerParts.Add(g2000Applied
                    ? T("footer.settingsG2000AppliedSuffix")
                    : T("footer.settingsG2000NotAppliedSuffix"));
            }

            FooterText.Text = string.Join(" ", footerParts);
        }
        catch (OperationCanceledException) when (_gasFlowLifetimeCts.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            EndGasFlowAction();
            EndMonitoringLifecycle();
        }
    }

    private async void AdvancedSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMonitoringLifecycle())
        {
            return;
        }

        try
        {
            if (_monitoringCts is not null)
            {
                ShowSetupWarning(T("message.liveHardwareChangeBlocked"));
                return;
            }

            if (!await SaveSettingsFromUiAsync())
            {
                return;
            }
            if (_shutdownPreparing)
            {
                return;
            }

            var window = new AdvancedSettingsWindow(_settings.Relay, NormalizeLanguage(_settings.Language))
            {
                Owner = this
            };

            if (window.ShowDialog() != true)
            {
                return;
            }

            var candidateSettings = _settings.Clone();
            candidateSettings.Relay = window.ResultSettings;
            Volatile.Write(ref _settings, candidateSettings);
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            if (_shutdownPreparing)
            {
                return;
            }

            _experimentRecorder?.UpdateSettings(_settings);
            if (!await RebuildOrRestartMonitoringAsync())
            {
                return;
            }

            if (_shutdownPreparing)
            {
                return;
            }

            BindSettingsToUi();
            ApplyLanguage();
            FooterText.Text = T("footer.advancedSaved");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            EndMonitoringLifecycle();
        }
    }

    private async void GitLabUploadSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMonitoringLifecycle())
        {
            return;
        }

        try
        {
            var window = new GitLabUploadSettingsWindow(
                _settings.ExperimentUpload,
                NormalizeLanguage(_settings.Language),
                _experimentCredentialStore)
            {
                Owner = this
            };
            if (window.ShowDialog() != true)
            {
                return;
            }
            if (_shutdownPreparing)
            {
                return;
            }

            _settings.ExperimentUpload = window.ResultSettings;
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            if (_shutdownPreparing)
            {
                return;
            }

            _experimentRecorder?.UpdateSettings(_settings);
            _experimentRecorder?.NotifyCredentialAvailability(window.CredentialAvailable);
            UpdateExperimentUploadStatus(
                _experimentRecorder?.UploadState ?? ExperimentUploadState.Disabled);
            FooterText.Text = T("upload.settings.saved");
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            EndMonitoringLifecycle();
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
        if (!TryBeginMonitoringLifecycle())
        {
            return;
        }

        try
        {
            if (!await SaveSettingsFromUiAsync())
            {
                return;
            }
            if (!ValidateBranchInterlockSetup(requireRestore: true))
            {
                _experimentRecorder?.RecordEvent(
                    "relay",
                    "restore-monitoring-control",
                    "blocked",
                    "Engineering validation did not pass.");
                return;
            }

            await ExecuteBranchInterlockCommandAsync(static controller => controller.CloseAllInterlocksAsync(CancellationToken.None));
            SetAllChannelStates(true);
            SetCurrentMode(T("mode.monitoring"));
            MainTabControl.SelectedItem = MonitorTab;
            FooterText.Text = FormatBranchInterlockFooter(T("footer.monitoringControlRestored"));
            _experimentRecorder?.RecordEvent(
                "relay",
                "restore-monitoring-control",
                "command-completed",
                "hardware_feedback=false");
        }
        catch (Exception ex)
        {
            _experimentRecorder?.RecordEvent("relay", "restore-monitoring-control", "failed", ex.Message);
            ShowError(ex);
        }
        finally
        {
            EndMonitoringLifecycle();
        }
    }

    private async void StopGasButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginGasFlowAction())
        {
            return;
        }

        try
        {
            await SaveAmc2100SettingsFromUiAsync();
            if (!ValidateAmc2100Setup(requireEnabled: true))
            {
                _experimentRecorder?.RecordEvent(
                    "amc2100",
                    "set-flow-zero",
                    "blocked",
                    "AMC2100 validation did not pass.");
                return;
            }

            await ExecuteGasFlowIoAsync(
                static (controller, cancellationToken) => controller.StopFlowAsync(cancellationToken));
            var actualFlow = await RefreshGasFlowAsync(force: true);
            SetCurrentMode(T("mode.engineering"));
            FooterText.Text = T("footer.gasStopped");
            _experimentRecorder?.RecordEvent(
                "amc2100",
                "set-flow-zero",
                "command-completed",
                $"target_ml_min=0; setpoint_readback=true; actual_flow_ml_min={FormatOptionalDoubleForEvidence(actualFlow)}");
        }
        catch (OperationCanceledException) when (_gasFlowLifetimeCts.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception ex)
        {
            _experimentRecorder?.RecordEvent("amc2100", "set-flow-zero", "failed", ex.Message);
            ShowError(ex);
        }
        finally
        {
            EndGasFlowAction();
        }
    }

    private async void G2000RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginMonitoringLifecycle())
        {
            _experimentRecorder?.RecordEvent("g2000", "connect-refresh", "blocked", "Another monitoring lifecycle action is active.");
            if (!_shutdownPreparing)
            {
                FooterText.Text = T("message.controlOperationBusy");
            }

            return;
        }

        try
        {
            await SaveG2000SettingsFromUiAsync();
            var controller = await EnsureG2000ControlAsync();
            if (controller is null)
            {
                _experimentRecorder?.RecordEvent(
                    "g2000",
                    "connect-refresh",
                    "blocked",
                    "G2000 validation or connection setup did not pass.");
                return;
            }

            UpdateG2000Telemetry(controller.Snapshot);
            FooterText.Text = T("footer.g2000Refreshed");
            _experimentRecorder?.RecordEvent("g2000", "connect-refresh", "software-completed");
        }
        catch (Exception ex)
        {
            _experimentRecorder?.RecordEvent("g2000", "connect-refresh", "failed", ex.Message);
            ShowError(ex);
        }
        finally
        {
            EndMonitoringLifecycle();
        }
    }

    private async void G2000HvAusButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async("set-hv-off", async controller =>
        {
            await controller.SetHvStateAsync(G2000HvState.HvAus, CancellationToken.None);
            FooterText.Text = T("footer.g2000HvAus");
            _experimentRecorder?.RecordEvent(
                "g2000",
                "set-hv-off",
                "command-completed",
                "hardware_feedback=false");
        });
    }

    private async void G2000HvReadyButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async("set-hv-ready", async controller =>
        {
            if (TryWarnAboutPendingG2000Setpoints(controller))
            {
                _experimentRecorder?.RecordEvent(
                    "g2000",
                    "set-hv-ready",
                    "blocked",
                    "Writable setpoints have not been applied.");
                return;
            }

            await controller.SetHvStateAsync(G2000HvState.HvReady, CancellationToken.None);
            FooterText.Text = T("footer.g2000HvReady");
            _experimentRecorder?.RecordEvent(
                "g2000",
                "set-hv-ready",
                "command-completed",
                "hardware_feedback=false");
        });
    }

    private async void G2000HvEinButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async("set-hv-on", async controller =>
        {
            if (TryWarnAboutPendingG2000Setpoints(controller))
            {
                _experimentRecorder?.RecordEvent(
                    "g2000",
                    "set-hv-on",
                    "blocked",
                    "Writable setpoints have not been applied.");
                return;
            }

            var needsLead = !controller.Snapshot.HvEnable && _settings.Relay.G2000Can.HvReadyLeadTimeMs > 0;
            await controller.SetHvStateAsync(G2000HvState.HvOn, CancellationToken.None);
            FooterText.Text = needsLead
                ? string.Format(CultureInfo.InvariantCulture, T("footer.g2000HvEinArmed"), _settings.Relay.G2000Can.HvReadyLeadTimeMs)
                : T("footer.g2000HvEin");
            _experimentRecorder?.RecordEvent(
                "g2000",
                "set-hv-on",
                "command-completed",
                $"ready_lead_ms={(needsLead ? _settings.Relay.G2000Can.HvReadyLeadTimeMs : 0)}; hardware_feedback=false");
        });
    }

    private async void G2000ApplySetpointsButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async("apply-setpoints", async controller =>
        {
            await ApplyG2000WritableSetpointsFromUiAsync(controller, T("footer.g2000SetpointsApplied"));
            _experimentRecorder?.RecordEvent(
                "g2000",
                "apply-setpoints",
                "command-completed",
                $"{FormatG2000SetpointsForEvidence(controller.TargetSetpoints)}; hardware_feedback=false");
        });
    }

    private async void G2000SetpointStepButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async("adjust-setpoint", async controller =>
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
            _experimentRecorder?.RecordEvent(
                "g2000",
                "adjust-setpoint",
                "command-completed",
                $"{tag}; {FormatG2000SetpointsForEvidence(controller.TargetSetpoints)}; hardware_feedback=false");
        });
    }

    private async void G2000StartAutomaticButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async("start-automatic-sequence", async controller =>
        {
            if (!ValidateTemperatureMonitorReadyForG2000Automatic())
            {
                _experimentRecorder?.RecordEvent(
                    "g2000",
                    "start-automatic-sequence",
                    "blocked",
                    "Temperature monitoring readiness validation did not pass.");
                return;
            }

            await SaveG2000SettingsFromUiAsync();
            if (!ValidateTemperatureMonitorReadyForG2000Automatic())
            {
                _experimentRecorder?.RecordEvent(
                    "g2000",
                    "start-automatic-sequence",
                    "blocked",
                    "Temperature or G2000 readiness changed while saving automatic settings.");
                return;
            }

            try
            {
                await controller.StartAutomaticSequenceAsync(_settings.Relay.G2000Can.StartupRecipe.Clone(), CancellationToken.None);
            }
            catch (G2000AutomaticStartBlockedException)
            {
                _experimentRecorder?.RecordEvent(
                    "g2000",
                    "start-automatic-sequence",
                    "blocked",
                    "G2000 reported an active fault or latched trip at the atomic start guard.");
                ShowSetupWarning(T("message.g2000AutomaticBlockedByInterlock"));
                return;
            }

            FooterText.Text = T("footer.g2000AutomaticStarted");
            _experimentRecorder?.RecordEvent(
                "g2000",
                "start-automatic-sequence",
                "command-completed",
                $"stage1_voltage_v={FormatDoubleForEvidence(_settings.Relay.G2000Can.StartupRecipe.Stage1VoltageV)}; " +
                $"stage1_duration_ms={_settings.Relay.G2000Can.StartupRecipe.Stage1DurationMs}; " +
                $"stage2_voltage_v={FormatDoubleForEvidence(_settings.Relay.G2000Can.StartupRecipe.Stage2VoltageV)}; " +
                "hardware_feedback=false");
        });
    }

    private async void G2000StopAutomaticButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteG2000Async("stop-automatic-sequence", async controller =>
        {
            await controller.StopAutomaticSequenceAsync(CancellationToken.None);
            FooterText.Text = T("footer.g2000AutomaticStopped");
            _experimentRecorder?.RecordEvent(
                "g2000",
                "stop-automatic-sequence",
                "command-completed",
                "hardware_feedback=false");
        });
    }

    private async void AmcSetpointStepButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var delta))
        {
            return;
        }

        if (!TryBeginGasFlowAction())
        {
            return;
        }

        try
        {
            if (AmcEnabledBox.IsChecked != true)
            {
                _experimentRecorder?.RecordEvent(
                    "amc2100",
                    "adjust-flow-setpoint",
                    "blocked",
                    "AMC2100 is disabled.");
                ShowSetupWarning(T("message.amcDisabled"));
                return;
            }

            if (!double.TryParse(AmcFallbackBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var current) ||
                !double.IsFinite(current) ||
                current < 0)
            {
                throw new InvalidOperationException(T("message.amcSetpointInvalid"));
            }
            var adjusted = Math.Max(0, current + delta);
            AmcFallbackBox.Text = adjusted.ToString("0.0", CultureInfo.InvariantCulture);
            await SaveAmc2100SettingsFromUiAsync();
            if (!ValidateAmc2100Setup(requireEnabled: true))
            {
                _experimentRecorder?.RecordEvent(
                    "amc2100",
                    "adjust-flow-setpoint",
                    "blocked",
                    $"delta_ml_min={FormatDoubleForEvidence(delta)}; AMC2100 validation did not pass.");
                return;
            }

            await ApplyConfiguredGasFlowAsync(
                "adjust-flow-setpoint",
                $"delta_ml_min={FormatDoubleForEvidence(delta)}");
        }
        catch (OperationCanceledException) when (_gasFlowLifetimeCts.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception ex)
        {
            _experimentRecorder?.RecordEvent("amc2100", "adjust-flow-setpoint", "failed", ex.Message);
            ShowError(ex);
        }
        finally
        {
            EndGasFlowAction();
        }
    }

    private async void RestoreGasButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginGasFlowAction())
        {
            return;
        }

        try
        {
            await SaveAmc2100SettingsFromUiAsync();
            if (!ValidateAmc2100Setup(requireEnabled: true))
            {
                _experimentRecorder?.RecordEvent(
                    "amc2100",
                    "set-flow-target",
                    "blocked",
                    "AMC2100 validation did not pass.");
                return;
            }

            await ApplyConfiguredGasFlowAsync("set-flow-target");
            SetCurrentMode(T("mode.engineering"));
        }
        catch (OperationCanceledException) when (_gasFlowLifetimeCts.IsCancellationRequested)
        {
            // Normal application shutdown.
        }
        catch (Exception ex)
        {
            _experimentRecorder?.RecordEvent("amc2100", "set-flow-target", "failed", ex.Message);
            ShowError(ex);
        }
        finally
        {
            EndGasFlowAction();
        }
    }

    private async Task ApplyConfiguredGasFlowAsync(string actionName, string additionalDetails = "")
    {
        var target = _settings.Amc2100.FallbackRestoreSetpointMlMin;
        await ExecuteGasFlowIoAsync(
            (controller, cancellationToken) => controller.SetTargetFlowAsync(target, cancellationToken));
        var actualFlow = await RefreshGasFlowAsync(force: true);
        FooterText.Text = string.Format(
            CultureInfo.InvariantCulture,
            T("footer.gasSetpointApplied"),
            target.ToString("0.0", CultureInfo.InvariantCulture));
        var details = $"target_ml_min={FormatDoubleForEvidence(target)}; setpoint_readback=true; " +
                      $"actual_flow_ml_min={FormatOptionalDoubleForEvidence(actualFlow)}";
        if (!string.IsNullOrWhiteSpace(additionalDetails))
        {
            details = $"{additionalDetails}; {details}";
        }

        _experimentRecorder?.RecordEvent(
            "amc2100",
            actionName,
            "command-completed",
            details);
    }

    private Task ExecuteGasFlowIoAsync(Func<IGasFlowController, CancellationToken, Task> action)
    {
        var controller = CreateGasFlowController();
        var cancellationToken = _gasFlowLifetimeCts.Token;
        return Task.Run(() => action(controller, cancellationToken), cancellationToken);
    }

    private Task<T> ExecuteGasFlowIoAsync<T>(Func<IGasFlowController, CancellationToken, Task<T>> action)
    {
        var controller = CreateGasFlowController();
        var cancellationToken = _gasFlowLifetimeCts.Token;
        return Task.Run(() => action(controller, cancellationToken), cancellationToken);
    }

    private bool TryBeginGasFlowAction()
    {
        if (_shutdownPreparing || !_gasFlowActionGate.Wait(0))
        {
            return false;
        }

        _isGasFlowActionBusy = true;
        SetGasFlowControlsEnabled(false);
        UpdateRunBoundaryButtons();
        return true;
    }

    private void EndGasFlowAction()
    {
        _gasFlowActionGate.Release();
        _isGasFlowActionBusy = false;
        if (!_shutdownPreparing)
        {
            SetGasFlowControlsEnabled(true);
            UpdateRunBoundaryButtons();
        }
    }

    private void SetGasFlowControlsEnabled(bool enabled)
    {
        AmcEnabledBox.IsEnabled = enabled;
        AmcPortBox.IsEnabled = enabled;
        AmcBaudBox.IsEnabled = enabled;
        AmcSlaveBox.IsEnabled = enabled;
        AmcFallbackBox.IsEnabled = enabled;
        AmcForceDigitalModeBox.IsEnabled = enabled;
        AmcSetpointDecreaseButton.IsEnabled = enabled;
        AmcSetpointIncreaseButton.IsEnabled = enabled;
        StopGasButton.IsEnabled = enabled;
        RestoreGasButton.IsEnabled = enabled;
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

        var dataDirectory = ResolveDataDirectory();

        _sampleLog = new CsvSampleLog(Path.Combine(dataDirectory, "temperature-history.csv"));
        var interlockSettings = new InterlockSettings(_settings.ThresholdC);
        _stateMachine = _stateMachine is null
            ? new InterlockStateMachine(interlockSettings)
            : _stateMachine.Reconfigure(interlockSettings);
        var reader = new TesseractCliTemperatureReader(
            new WindowCapture(),
            new TemperatureTextParser(),
            () => Volatile.Read(ref _settings));
        var autoReset = new AutoResetOptions(_settings.AutoResetEnabled, _settings.RecoveryThresholdC, _settings.RecoveryStableSeconds);
        var monitoringOutput = new ExperimentRelayFailureObserver(
            CreateProcessOutputController(),
            (action, outcome, details) => _experimentRecorder?.RecordEvent(
                "interlock",
                action,
                outcome,
                details));
        _monitoringService = new MonitoringService(reader, monitoringOutput, _sampleLog, new SystemClock(), _stateMachine, autoReset);
        _monitoringService.SampleRecorded += MonitoringService_SampleRecorded;
        UpdateGasFlowDisplay();
        UpdateG2000ModeAvailability();
    }

    private string ResolveDataDirectory()
    {
        return Path.IsPathRooted(_settings.DataDirectory)
            ? _settings.DataDirectory
            : Path.Combine(AppContext.BaseDirectory, _settings.DataDirectory);
    }

    private void DetachMonitoringService()
    {
        if (_monitoringService is not null)
        {
            _monitoringService.SampleRecorded -= MonitoringService_SampleRecorded;
            _monitoringService = null;
        }
    }

    private async Task RebuildServicesPreservingG2000Async(bool ensureG2000Connected = true)
    {
        var runtimeKeyUnchanged = _relayBankController is not null &&
            string.Equals(_relayRuntimeKey, CreateRelayRuntimeKey(_settings.Relay), StringComparison.Ordinal);
        if (runtimeKeyUnchanged)
        {
            BuildMonitoringService();
            if (ensureG2000Connected)
            {
                await InitializeG2000ControllerAsync();
            }

            return;
        }

        var restoreLatchedTrip = _stateMachine?.IsTripped == true;
        BuildServices();
        if (restoreLatchedTrip)
        {
            _stateMachine!.RequireStopConfirmation();
            try
            {
                var relayAction = await CreateProcessOutputController().StopAsync(CancellationToken.None);
                if (relayAction != RelayAction.StopSent)
                {
                    throw new InvalidOperationException($"Interlock stop returned unexpected action {relayAction}.");
                }

                _stateMachine.ConfirmStopSent();
                SetAllChannelStates(false);
                _experimentRecorder?.RecordEvent(
                    "interlock",
                    "reassert-stop-after-output-rebuild",
                    "success",
                    "A latched temperature trip was re-applied to the replacement output controller.");
            }
            catch (Exception ex)
            {
                _experimentRecorder?.RecordEvent(
                    "interlock",
                    "reassert-stop-after-output-rebuild",
                    "failed",
                    ex.Message);
                throw;
            }
        }

        if (ensureG2000Connected)
        {
            await InitializeG2000ControllerAsync();
        }
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
        var settingsSnapshot = _settings.Amc2100.Clone();
        IGasFlowController controller = !settingsSnapshot.Enabled
            ? new NoOpGasFlowController()
            : new Amc2100GasFlowController(settingsSnapshot);
        return new SynchronizedGasFlowController(controller, _gasFlowGate);
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
        _lastG2000ConnectionError = null;
        return _g2000Controller;
    }

    private async Task ExecuteG2000Async(
        string actionName,
        Func<IG2000Controller, Task> action)
    {
        if (!TryBeginMonitoringLifecycle())
        {
            _experimentRecorder?.RecordEvent("g2000", actionName, "blocked", "Another monitoring lifecycle action is active.");
            if (!_shutdownPreparing)
            {
                FooterText.Text = T("message.controlOperationBusy");
            }

            return;
        }

        try
        {
            var controller = await EnsureG2000ControlAsync();
            if (controller is null)
            {
                _experimentRecorder?.RecordEvent(
                    "g2000",
                    actionName,
                    "blocked",
                    "G2000 validation or connection setup did not pass.");
                return;
            }

            await action(controller);
            UpdateG2000Telemetry(controller.Snapshot);
        }
        catch (Exception ex)
        {
            _experimentRecorder?.RecordEvent("g2000", actionName, "failed", ex.Message);
            ShowError(ex);
        }
        finally
        {
            EndMonitoringLifecycle();
        }
    }

    private async Task RunMonitoringLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _monitoringService!.RunAsync(TimeSpan.FromMilliseconds(_settings.PollIntervalMs), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop path.
        }
        catch (Exception ex)
        {
            _experimentRecorder?.RecordEvent("monitoring", "poll-loop", "failed", ex.Message);
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_monitoringCts is null || _monitoringCts.Token != cancellationToken)
                {
                    return;
                }

                var monitoringTask = StopMonitoring(T("footer.monitoringStopped"));
                if (monitoringTask is not null)
                {
                    _ = CompleteFailedMonitoringRunAsync(monitoringTask);
                }
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

        var g2000Snapshot = _g2000Controller?.Snapshot;
        if (_g2000Controller?.IsTripLatched == true || g2000Snapshot?.Fault == true)
        {
            ShowSetupWarning(T("message.g2000AutomaticBlockedByInterlock"));
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

    private bool ValidateMonitoringSetup() => ValidateMonitoringSetup(_settings);

    private bool ValidateMonitoringSetup(AppSettings settings)
    {
        if (!settings.Roi.IsConfigured)
        {
            ShowSetupWarning(T("message.roiMissing"));
            return false;
        }

        if (!RuntimePathResolver.ExecutableExists(settings.Ocr.TesseractExePath))
        {
            ShowSetupWarning(string.Format(CultureInfo.InvariantCulture, T("message.tesseractMissing"), settings.Ocr.TesseractExePath));
            return false;
        }

        if (settings.PollIntervalMs <= 0)
        {
            ShowSetupWarning(T("message.pollIntervalInvalid"));
            return false;
        }

        try
        {
            _ = new WindowCapture().GetWindowBounds(settings.WindowTitleContains);
        }
        catch
        {
            ShowSetupWarning(string.Format(CultureInfo.InvariantCulture, T("message.hikmicroMissing"), settings.WindowTitleContains));
            return false;
        }

        if (!ValidateMonitoringRelaySetup(settings, requireRestore: true))
        {
            return false;
        }

        if (settings.Relay.ResolveMode() == RelayControllerMode.G2000Can && settings.Relay.DryRun)
        {
            ShowSetupWarning(T("message.g2000LiveMonitoringNeedsRelay"));
            return false;
        }

        return true;
    }

    private bool ValidateAmc2100Setup(bool requireEnabled = false)
    {
        var settings = _settings.Amc2100;
        if (!settings.Enabled)
        {
            if (requireEnabled)
            {
                ShowSetupWarning(T("message.amcDisabled"));
                return false;
            }

            return true;
        }

        if (string.IsNullOrWhiteSpace(settings.PortName))
        {
            ShowSetupWarning(T("message.amcPortRequired"));
            return false;
        }

        if (settings.BaudRate <= 0)
        {
            ShowSetupWarning(T("message.amcBaudInvalid"));
            return false;
        }

        if (settings.SlaveAddress is < 1 or > 247)
        {
            ShowSetupWarning(T("message.amcSlaveInvalid"));
            return false;
        }

        if (!double.IsFinite(settings.FallbackRestoreSetpointMlMin) ||
            settings.FallbackRestoreSetpointMlMin < 0)
        {
            ShowSetupWarning(T("message.amcSetpointInvalid"));
            return false;
        }

        var availablePorts = SerialPort.GetPortNames();
        if (!availablePorts.Contains(settings.PortName, StringComparer.OrdinalIgnoreCase))
        {
            ShowSetupWarning(string.Format(CultureInfo.InvariantCulture, T("message.amcComMissing"), settings.PortName));
            return false;
        }

        return true;
    }

    private bool ValidateMonitoringRelaySetup(bool requireRestore, int? specificChannelNumber = null)
        => ValidateMonitoringRelaySetup(_settings, requireRestore, specificChannelNumber);

    private bool ValidateMonitoringRelaySetup(
        AppSettings settings,
        bool requireRestore,
        int? specificChannelNumber = null)
    {
        settings.Relay.Normalize();

        return settings.Relay.ResolveMode() switch
        {
            RelayControllerMode.DryRun => true,
            RelayControllerMode.Serial => ValidateSerialRelaySetup(settings.Relay, requireRestore, specificChannelNumber),
            RelayControllerMode.G2000Can => ValidateG2000CanSetup(settings.Relay.G2000Can) &&
                                                ValidatePhysicalInterlockSetupForCanMode(settings.Relay, requireRestore, specificChannelNumber),
            _ => false
        };
    }

    private bool ValidateBranchInterlockSetup(bool requireRestore, int? specificChannelNumber = null)
    {
        _settings.Relay.Normalize();

        return _settings.Relay.ResolveMode() switch
        {
            RelayControllerMode.DryRun => true,
            RelayControllerMode.Serial => ValidateSerialRelaySetup(_settings.Relay, requireRestore, specificChannelNumber),
            RelayControllerMode.G2000Can => ValidatePhysicalInterlockSetupForCanMode(_settings.Relay, requireRestore, specificChannelNumber),
            _ => false
        };
    }

    private bool ValidatePhysicalInterlockSetupForCanMode(
        RelaySettings relaySettings,
        bool requireRestore,
        int? specificChannelNumber)
    {
        if (relaySettings.DryRun)
        {
            ShowSetupWarning(T("message.branchRelayDryRun"));
            return false;
        }

        return ValidateSerialRelaySetup(relaySettings, requireRestore, specificChannelNumber);
    }

    private bool ValidateSerialRelaySetup(
        RelaySettings relaySettings,
        bool requireRestore,
        int? specificChannelNumber)
    {
        var availablePorts = SerialPort.GetPortNames();
        if (!availablePorts.Contains(relaySettings.PortName, StringComparer.OrdinalIgnoreCase))
        {
            ShowSetupWarning(string.Format(CultureInfo.InvariantCulture, T("message.comMissing"), relaySettings.PortName));
            return false;
        }

        var channels = relaySettings.Channels
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

    private bool ValidateG2000CanSetup() => ValidateG2000CanSetup(_settings.Relay.G2000Can);

    private bool ValidateG2000CanSetup(G2000CanSettings settings)
    {
        if (!PcanChannelParser.TryParse(settings.Channel, out _))
        {
            ShowSetupWarning(T("message.g2000CanChannelInvalid"));
            return false;
        }

        if (settings.NodeId > 0x7E)
        {
            ShowSetupWarning(T("message.g2000NodeInvalid"));
            return false;
        }

        if (settings.CommandPeriodMs <= 0)
        {
            ShowSetupWarning(T("message.g2000CommandPeriodInvalid"));
            return false;
        }

        if (settings.ReadPollIntervalMs <= 0)
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
        _experimentRecorder?.RecordTemperature(sample);
        if (sample.RelayAction == RelayAction.StopSent)
        {
            _experimentRecorder?.RecordEvent(
                "interlock",
                "temperature-trip",
                "command-completed",
                $"temperature_c={FormatDoubleForEvidence(sample.TemperatureC)}; {sample.AlarmReason}; hardware_feedback=false");
        }
        else if (sample.RelayAction == RelayAction.ResetSent)
        {
            _experimentRecorder?.RecordEvent(
                "interlock",
                "automatic-recovery",
                "command-completed",
                $"temperature_c={FormatDoubleForEvidence(sample.TemperatureC)}; hardware_feedback=false");
        }

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
        _experimentRecorder?.RecordG2000Telemetry(snapshot);
        var shouldSchedule = false;
        lock (_g2000TelemetryUiSync)
        {
            _pendingG2000TelemetryUiSnapshot = snapshot.Clone();
            if (!_g2000TelemetryUiUpdateScheduled)
            {
                _g2000TelemetryUiUpdateScheduled = true;
                shouldSchedule = true;
            }
        }

        if (shouldSchedule)
        {
            SchedulePendingG2000TelemetryUiUpdate();
        }
    }

    private void SchedulePendingG2000TelemetryUiUpdate()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            lock (_g2000TelemetryUiSync)
            {
                _pendingG2000TelemetryUiSnapshot = null;
                _g2000TelemetryUiUpdateScheduled = false;
            }

            return;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(ProcessPendingG2000TelemetryUiUpdate));
        }
        catch (InvalidOperationException)
        {
            lock (_g2000TelemetryUiSync)
            {
                _pendingG2000TelemetryUiSnapshot = null;
                _g2000TelemetryUiUpdateScheduled = false;
            }
        }
    }

    private void ProcessPendingG2000TelemetryUiUpdate()
    {
        G2000TelemetrySnapshot? snapshot;
        lock (_g2000TelemetryUiSync)
        {
            snapshot = _pendingG2000TelemetryUiSnapshot;
            _pendingG2000TelemetryUiSnapshot = null;
        }

        if (snapshot is not null)
        {
            try
            {
                UpdateG2000Telemetry(snapshot);
            }
            catch (Exception ex)
            {
                _experimentRecorder?.RecordEvent("g2000", "ui-telemetry-update", "failed", ex.Message);
            }
        }

        var scheduleAgain = false;
        lock (_g2000TelemetryUiSync)
        {
            if (_pendingG2000TelemetryUiSnapshot is null)
            {
                _g2000TelemetryUiUpdateScheduled = false;
            }
            else
            {
                scheduleAgain = true;
            }
        }

        if (scheduleAgain)
        {
            SchedulePendingG2000TelemetryUiUpdate();
        }
    }

    private void UpdateG2000Telemetry(G2000TelemetrySnapshot snapshot)
    {
        _lastG2000Snapshot = snapshot.Clone();
        if (snapshot.Connected && snapshot.CommunicationHealthy && snapshot.LastReceivedAt is not null)
        {
            _lastG2000ConnectionError = null;
        }

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

    private Task? StopMonitoring(string message)
    {
        var wasMonitoring = _monitoringCts is not null;
        var monitoringTask = _monitoringTask;
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
        _monitoringCts = null;
        _monitoringTask = null;
        _monitoringStartedAt = null;
        SetStatus(_stateMachine.IsTripped ? MonitorStatus.Tripped : MonitorStatus.Idle);
        FooterText.Text = message;
        if (wasMonitoring)
        {
            _experimentRecorder?.RecordEvent("monitoring", "stopped", "success");
        }

        return wasMonitoring ? monitoringTask : null;
    }

    private async Task CompleteFailedMonitoringRunAsync(Task monitoringTask)
    {
        if (_shutdownPreparing)
        {
            return;
        }

        await _monitoringLifecycleGate.WaitAsync();
        _isRunFinalizing = true;
        UpdateRunBoundaryButtons();
        try
        {
            if (_shutdownPreparing)
            {
                return;
            }

            await AwaitMonitoringTaskAsync(monitoringTask, CancellationToken.None);
            await FinalizeExperimentRunAfterStopAsync("monitoring-failed");
        }
        finally
        {
            EndMonitoringLifecycle();
        }
    }

    private bool TryBeginMonitoringLifecycle()
    {
        if (_shutdownPreparing || !_monitoringLifecycleGate.Wait(0))
        {
            return false;
        }

        _isRunFinalizing = true;
        UpdateRunBoundaryButtons();
        return true;
    }

    private void EndMonitoringLifecycle()
    {
        _isRunFinalizing = false;
        UpdateRunBoundaryButtons();
        _monitoringLifecycleGate.Release();
    }

    private void UpdateRunBoundaryButtons()
    {
        if (!IsLoaded)
        {
            return;
        }

        StartButton.IsEnabled = !_shutdownPreparing && !_isRunFinalizing && _monitoringCts is null;
        StopButton.IsEnabled = !_shutdownPreparing && !_isRunFinalizing && _monitoringCts is not null;
        SaveSettingsButton.IsEnabled = !_shutdownPreparing && !_isRunFinalizing && !_isGasFlowActionBusy;
        ExportExperimentButton.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        ExportExperimentMenuItem.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        AdvancedSettingsMenuItem.IsEnabled = !_shutdownPreparing && !_isRunFinalizing && _monitoringCts is null;
        GitLabUploadSettingsMenuItem.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        SelectRoiButton.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        SelectRoiMenuItem.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        G2000ControlPanel.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        EngineeringTab.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        TestRelayButton.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        TestRelayResetButton.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        TripAllEngineeringButton.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        RestoreAllEngineeringButton.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        foreach (var channel in _channelUis)
        {
            channel.OpenButton.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
            channel.CloseButton.IsEnabled = !_shutdownPreparing && !_isRunFinalizing;
        }
    }

    private static async Task AwaitMonitoringTaskAsync(
        Task? monitoringTask,
        CancellationToken cancellationToken)
    {
        if (monitoringTask is null)
        {
            return;
        }

        try
        {
            await monitoringTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the normal monitoring stop path or the shutdown deadline.
        }
        catch
        {
            // RunMonitoringLoopAsync already records and displays polling failures.
        }
    }

    private async Task FinalizeExperimentRunAfterStopAsync(string reason)
    {
        var coordinator = _experimentRecorder;
        if (coordinator is null)
        {
            return;
        }

        var result = await coordinator.FinalizeRunAsync(
            _settings,
            reason,
            startNextRun: true,
            CancellationToken.None);
        if (!result.Created && !string.IsNullOrWhiteSpace(result.FailureKind))
        {
            FooterText.Text = T("upload.packagingFailed");
        }
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

    private async Task<bool> SaveSettingsFromUiAsync()
    {
        var candidateSettings = BuildSettingsCandidateFromUi();
        if (_monitoringCts is not null)
        {
            if (!CanApplyCandidateDuringMonitoring(candidateSettings, showWarning: true))
            {
                return false;
            }

            if (!ValidateMonitoringSetup(candidateSettings))
            {
                _experimentRecorder?.RecordEvent(
                    "settings",
                    "validate-before-live-apply",
                    "blocked",
                    "The candidate setup did not pass validation; the existing monitoring loop and settings remain active.");
                return false;
            }
        }

        await ApplySettingsCandidateAtRuntimeBoundaryAsync(candidateSettings);
        await PersistCurrentSettingsAsync();
        return true;
    }

    private async Task ApplySettingsCandidateAtRuntimeBoundaryAsync(AppSettings candidateSettings)
    {
        if (_monitoringCts is null)
        {
            Volatile.Write(ref _settings, candidateSettings);
            return;
        }

        if (!CanApplyCandidateDuringMonitoring(candidateSettings, showWarning: false))
        {
            throw new InvalidOperationException(T("message.liveHardwareChangeBlocked"));
        }

        if (_monitoringService is null)
        {
            throw new InvalidOperationException(T("message.monitoringServiceUnavailable"));
        }

        await _monitoringService.ApplyRuntimeConfigurationAsync(
            new InterlockSettings(candidateSettings.ThresholdC),
            new AutoResetOptions(
                candidateSettings.AutoResetEnabled,
                candidateSettings.RecoveryThresholdC,
                candidateSettings.RecoveryStableSeconds),
            TimeSpan.FromMilliseconds(candidateSettings.PollIntervalMs),
            () => Volatile.Write(ref _settings, candidateSettings),
            CancellationToken.None);
    }

    private bool CanApplyCandidateDuringMonitoring(AppSettings candidateSettings, bool showWarning)
    {
        if (string.Equals(_relayRuntimeKey, CreateRelayRuntimeKey(candidateSettings.Relay), StringComparison.Ordinal))
        {
            return true;
        }

        _experimentRecorder?.RecordEvent(
            "settings",
            "apply-live",
            "blocked",
            "Relay or G2000 connection settings changed while monitoring was running; no candidate settings were applied.");
        if (showWarning)
        {
            ShowSetupWarning(T("message.liveHardwareChangeBlocked"));
        }

        return false;
    }

    private AppSettings BuildSettingsCandidateFromUi()
    {
        var candidate = _settings.Clone();
        var amcSettings = ReadAmc2100SettingsFromUi();
        var g2000Settings = candidate.Relay.G2000Can.Clone();
        var thresholdC = ParseDouble(ThresholdBox.Text, nameof(_settings.ThresholdC));
        var recoveryThresholdC = ParseDouble(RecoveryThresholdBox.Text, nameof(_settings.RecoveryThresholdC));
        var recoveryStableSeconds = ParseInt(StableSecondsBox.Text, nameof(_settings.RecoveryStableSeconds));
        var pollIntervalMs = ParseInt(IntervalBox.Text, nameof(_settings.PollIntervalMs));
        var stage1DurationMs = ParseInt(G2000Stage1DurationBox.Text, nameof(g2000Settings.StartupRecipe.Stage1DurationMs));
        if (!TemperatureTextParser.IsPlausibleReading(thresholdC))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                T("message.thresholdInvalid"),
                TemperatureTextParser.MinimumPlausibleTemperatureC,
                TemperatureTextParser.MaximumPlausibleTemperatureC));
        }

        if (!TemperatureTextParser.IsPlausibleReading(recoveryThresholdC))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                T("message.recoveryThresholdInvalid"),
                TemperatureTextParser.MinimumPlausibleTemperatureC,
                TemperatureTextParser.MaximumPlausibleTemperatureC));
        }

        if (recoveryThresholdC >= thresholdC)
        {
            throw new InvalidOperationException(T("message.recoveryThresholdOrderInvalid"));
        }

        if (recoveryStableSeconds <= 0)
        {
            throw new InvalidOperationException(T("message.recoveryStableInvalid"));
        }

        if (pollIntervalMs <= 0)
        {
            throw new InvalidOperationException(T("message.pollIntervalInvalid"));
        }

        g2000Settings.WritableSetpoints.VoltageV = ParseDouble(G2000VoltageBox.Text, nameof(g2000Settings.WritableSetpoints.VoltageV));
        g2000Settings.WritableSetpoints.FrequencyKhz = ParseDouble(G2000FrequencyBox.Text, nameof(g2000Settings.WritableSetpoints.FrequencyKhz));
        g2000Settings.WritableSetpoints.DutyPercent = ParseDouble(G2000DutyBox.Text, nameof(g2000Settings.WritableSetpoints.DutyPercent));
        g2000Settings.WritableSetpoints.TonMs = ParseDouble(G2000TonBox.Text, nameof(g2000Settings.WritableSetpoints.TonMs));
        g2000Settings.WritableSetpoints.ToffMs = ParseDouble(G2000ToffBox.Text, nameof(g2000Settings.WritableSetpoints.ToffMs));
        g2000Settings.StartupRecipe.Stage1VoltageV = ParseDouble(G2000Stage1VoltageBox.Text, nameof(g2000Settings.StartupRecipe.Stage1VoltageV));
        g2000Settings.StartupRecipe.Stage1DurationMs = stage1DurationMs;
        g2000Settings.StartupRecipe.Stage2VoltageV = ParseDouble(G2000Stage2VoltageBox.Text, nameof(g2000Settings.StartupRecipe.Stage2VoltageV));
        g2000Settings.StartupRecipe.Stage2HoldEnabled = G2000Stage2HoldBox.IsChecked == true;
        g2000Settings.StartupRecipe.EnterHvReadyBeforeRun = G2000EnterHvReadyBox.IsChecked == true;
        g2000Settings.StartupRecipe.EnterHvOnAtStart = G2000EnterHvOnBox.IsChecked == true;
        g2000Settings.RecoveryPolicy = ((TripRecoveryPolicy?)G2000RecoveryPolicyBox.SelectedValue ?? TripRecoveryPolicy.HoldHvAus).ToString();
        g2000Settings.Normalize();
        ValidateG2000U2SettingsForUi(g2000Settings);
        g2000Settings.ValidateWritableSetpoints(g2000Settings.WritableSetpoints);
        g2000Settings.ValidateStartupRecipe(g2000Settings.StartupRecipe);

        candidate.Language = NormalizeLanguage(LanguageBox.SelectedValue?.ToString() ?? candidate.Language);
        candidate.WindowTitleContains = WindowTitleBox.Text.Trim();
        candidate.ThresholdC = thresholdC;
        candidate.RecoveryThresholdC = recoveryThresholdC;
        candidate.RecoveryStableSeconds = recoveryStableSeconds;
        candidate.AutoResetEnabled = AutoResetBox.IsChecked == true;
        candidate.PollIntervalMs = pollIntervalMs;
        candidate.Ocr.TesseractExePath = TesseractPathBox.Text.Trim();
        candidate.Relay.PortName = PortBox.Text.Trim();
        candidate.Relay.DryRun = DryRunBox.IsChecked == true;
        candidate.Relay.G2000Can = g2000Settings;
        candidate.Relay.Normalize();
        candidate.Amc2100 = amcSettings;
        return candidate;
    }

    private async Task PersistCurrentSettingsAsync()
    {
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        _experimentRecorder?.UpdateSettings(_settings);
        _experimentRecorder?.RecordEvent(
            "settings",
            "saved",
            "success",
            $"threshold_c={FormatDoubleForEvidence(_settings.ThresholdC)}; " +
            $"recovery_c={FormatDoubleForEvidence(_settings.RecoveryThresholdC)}; " +
            $"recovery_stable_s={_settings.RecoveryStableSeconds}; " +
            $"auto_reset_enabled={_settings.AutoResetEnabled}; " +
            $"amc_enabled={_settings.Amc2100.Enabled}; " +
            $"amc_target_ml_min={FormatDoubleForEvidence(_settings.Amc2100.FallbackRestoreSetpointMlMin)}");
        ApplyG2000RecoveryPolicyToController();
        UpdateGasFlowDisplay();
        UpdateG2000SettingsSummary();
    }

    private async Task SaveAmc2100SettingsFromUiAsync()
    {
        var candidateSettings = _settings.Clone();
        candidateSettings.Amc2100 = ReadAmc2100SettingsFromUi();
        Volatile.Write(ref _settings, candidateSettings);
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        _experimentRecorder?.UpdateSettings(_settings);
        UpdateGasFlowDisplay();
    }

    private Amc2100Settings ReadAmc2100SettingsFromUi()
    {
        var candidate = _settings.Amc2100.Clone();
        candidate.Enabled = AmcEnabledBox.IsChecked == true;
        candidate.ForceDigitalControlMode = AmcForceDigitalModeBox.IsChecked == true;

        var portName = AmcPortBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(portName))
        {
            candidate.PortName = portName;
        }
        else if (candidate.Enabled)
        {
            throw new InvalidOperationException(T("message.amcPortRequired"));
        }

        if (int.TryParse(AmcBaudBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baudRate) &&
            baudRate > 0)
        {
            candidate.BaudRate = baudRate;
        }
        else if (candidate.Enabled)
        {
            throw new InvalidOperationException(T("message.amcBaudInvalid"));
        }

        if (int.TryParse(AmcSlaveBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slaveAddress) &&
            slaveAddress is >= 1 and <= 247)
        {
            candidate.SlaveAddress = slaveAddress;
        }
        else if (candidate.Enabled)
        {
            throw new InvalidOperationException(T("message.amcSlaveInvalid"));
        }

        if (double.TryParse(AmcFallbackBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var targetFlow) &&
            double.IsFinite(targetFlow) &&
            targetFlow >= 0)
        {
            candidate.FallbackRestoreSetpointMlMin = targetFlow;
        }
        else if (candidate.Enabled)
        {
            throw new InvalidOperationException(T("message.amcSetpointInvalid"));
        }

        return candidate;
    }

    private async Task SaveG2000SettingsFromUiAsync()
    {
        var candidateSettings = _settings.Clone();
        var candidate = candidateSettings.Relay.G2000Can;
        candidate.WritableSetpoints.VoltageV = ParseDouble(G2000VoltageBox.Text, nameof(candidate.WritableSetpoints.VoltageV));
        candidate.WritableSetpoints.FrequencyKhz = ParseDouble(G2000FrequencyBox.Text, nameof(candidate.WritableSetpoints.FrequencyKhz));
        candidate.WritableSetpoints.DutyPercent = ParseDouble(G2000DutyBox.Text, nameof(candidate.WritableSetpoints.DutyPercent));
        candidate.WritableSetpoints.TonMs = ParseDouble(G2000TonBox.Text, nameof(candidate.WritableSetpoints.TonMs));
        candidate.WritableSetpoints.ToffMs = ParseDouble(G2000ToffBox.Text, nameof(candidate.WritableSetpoints.ToffMs));
        candidate.StartupRecipe.Stage1VoltageV = ParseDouble(G2000Stage1VoltageBox.Text, nameof(candidate.StartupRecipe.Stage1VoltageV));
        candidate.StartupRecipe.Stage1DurationMs = ParseInt(G2000Stage1DurationBox.Text, nameof(candidate.StartupRecipe.Stage1DurationMs));
        candidate.StartupRecipe.Stage2VoltageV = ParseDouble(G2000Stage2VoltageBox.Text, nameof(candidate.StartupRecipe.Stage2VoltageV));
        candidate.StartupRecipe.Stage2HoldEnabled = G2000Stage2HoldBox.IsChecked == true;
        candidate.StartupRecipe.EnterHvReadyBeforeRun = G2000EnterHvReadyBox.IsChecked == true;
        candidate.StartupRecipe.EnterHvOnAtStart = G2000EnterHvOnBox.IsChecked == true;
        candidate.RecoveryPolicy = ((TripRecoveryPolicy?)G2000RecoveryPolicyBox.SelectedValue ?? TripRecoveryPolicy.HoldHvAus).ToString();
        candidate.Normalize();
        ValidateG2000U2SettingsForUi(candidate);
        candidate.ValidateWritableSetpoints(candidate.WritableSetpoints);
        candidate.ValidateStartupRecipe(candidate.StartupRecipe);
        Volatile.Write(ref _settings, candidateSettings);
        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        _experimentRecorder?.UpdateSettings(_settings);
        ApplyG2000RecoveryPolicyToController();
        UpdateG2000SettingsSummary();
    }

    private void ValidateG2000U2SettingsForUi(G2000CanSettings? candidate = null)
    {
        var can = candidate ?? _settings.Relay.G2000Can;
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
            var candidateSettings = _settings.Clone();
            candidateSettings.Language = NormalizeLanguage(LanguageBox.SelectedValue?.ToString() ?? "en");
            Volatile.Write(ref _settings, candidateSettings);
            ApplyLanguage();
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            _experimentRecorder?.UpdateSettings(_settings);
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
            _experimentRecorder?.UpdateSettings(_settings);
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
        var candidateSettings = _settings.Clone();
        candidateSettings.Relay.G2000Can.RecoveryPolicy = policy.ToString();
        Volatile.Write(ref _settings, candidateSettings);
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
        ExportExperimentMenuItem.Header = T("menu.exportExperiment");
        ExitMenuItem.Header = T("menu.exit");
        ToolsMenu.Header = T("menu.tools");
        SelectRoiMenuItem.Header = T("button.selectRoi");
        GuideMenuItem.Header = T("button.guide");
        AdvancedSettingsMenuItem.Header = T("menu.advanced");
        GitLabUploadSettingsMenuItem.Header = T("menu.gitLabUploadSettings");
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
        ExportExperimentButton.Content = T("button.exportExperiment");
        UploadStatusLabel.Text = T("upload.statusLabel");
        UpdateExperimentUploadStatus(
            _experimentRecorder?.UploadState ?? ExperimentUploadState.Disabled);
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
        LanguageQuickLabel.Text = "Language / Sprache";
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
        SetToolTip(ExportExperimentButton, "tooltip.exportExperiment");
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
        SetToolTip(AmcSetpointDecreaseButton, "tooltip.amcSetpointStep");
        SetToolTip(AmcSetpointIncreaseButton, "tooltip.amcSetpointStep");
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
        SetToolTip(ExportExperimentMenuItem, "tooltip.exportExperiment");
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

    private void ExperimentRecorder_UploadStateChanged(ExperimentUploadState state)
    {
        Dispatcher.BeginInvoke(() => UpdateExperimentUploadStatus(state));
    }

    private void UpdateExperimentUploadStatus(ExperimentUploadState state)
    {
        if (UploadStatusText is null)
        {
            return;
        }

        UploadStatusText.Text = T($"upload.status.{state}");
        UploadStatusText.Foreground = state switch
        {
            ExperimentUploadState.Uploaded => Brushes.ForestGreen,
            ExperimentUploadState.Failed => Brushes.Firebrick,
            ExperimentUploadState.MissingCredential => Brushes.DarkOrange,
            _ => Brushes.SlateGray
        };
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
        var engineeringControlsEnabled = !_shutdownPreparing && !_isRunFinalizing;

        TestRelayButton.IsEnabled = engineeringControlsEnabled;
        TestRelayResetButton.IsEnabled = engineeringControlsEnabled;
        TripAllEngineeringButton.IsEnabled = engineeringControlsEnabled;
        RestoreAllEngineeringButton.IsEnabled = engineeringControlsEnabled;

        var branchInterlockTooltip = BuildBranchInterlockTooltip(relayMode, _settings?.Relay.DryRun == true);
        TestRelayButton.ToolTip = branchInterlockTooltip;
        TestRelayResetButton.ToolTip = branchInterlockTooltip;
        TripAllEngineeringButton.ToolTip = branchInterlockTooltip;
        RestoreAllEngineeringButton.ToolTip = branchInterlockTooltip;

        foreach (var channel in _channelUis)
        {
            channel.OpenButton.IsEnabled = engineeringControlsEnabled;
            channel.CloseButton.IsEnabled = engineeringControlsEnabled;
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

    private async Task<double?> RefreshGasFlowAsync(bool force = false)
    {
        if (_settings is null)
        {
            return null;
        }

        var activeRead = _gasFlowReadTask;
        if (activeRead is { IsCompleted: false })
        {
            if (!force)
            {
                return _lastGasFlowMlMin;
            }

            try
            {
                await activeRead;
            }
            catch
            {
                // The forced read below provides the current result.
            }
        }

        var readTask = ReadGasFlowCoreAsync(force);
        _gasFlowReadTask = readTask;
        try
        {
            return await readTask;
        }
        finally
        {
            if (ReferenceEquals(_gasFlowReadTask, readTask))
            {
                _gasFlowReadTask = null;
            }
            UpdateGasFlowDisplay();
        }
    }

    private async Task<double?> ReadGasFlowCoreAsync(bool force)
    {
        if (!_settings.Amc2100.Enabled)
        {
            _lastGasFlowMlMin = null;
            return null;
        }

        if (!force && !ValidateAmcPortExistsSilently())
        {
            _lastGasFlowMlMin = null;
            _experimentRecorder?.RecordGasFlow(
                null,
                enabled: true,
                "unavailable",
                "Configured AMC2100 serial port was not found.");
            return null;
        }

        try
        {
            _lastGasFlowMlMin = await ExecuteGasFlowIoAsync(
                static (controller, cancellationToken) => controller.ReadActualFlowAsync(cancellationToken));
            _experimentRecorder?.RecordGasFlow(
                _lastGasFlowMlMin,
                enabled: true,
                "success",
                force ? "forced read" : "periodic read");
            return _lastGasFlowMlMin;
        }
        catch (OperationCanceledException) when (_gasFlowLifetimeCts.IsCancellationRequested)
        {
            _lastGasFlowMlMin = null;
            return null;
        }
        catch (Exception ex)
        {
            _lastGasFlowMlMin = null;
            _experimentRecorder?.RecordGasFlow(null, enabled: true, "error", ex.Message);
            return null;
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
        if (_shutdownPreparing ||
            _monitoringCts is not null ||
            _monitoringTask is { IsCompleted: false })
        {
            return;
        }

        _monitoringCts = new CancellationTokenSource();
        var cancellationToken = _monitoringCts.Token;
        MarkMonitoringSessionStarted();
        SetStatus(_stateMachine.IsTripped ? MonitorStatus.Tripped : MonitorStatus.Monitoring);
        SetCurrentMode(T("mode.monitoring"));
        FooterText.Text = footerText;
        _experimentRecorder?.RecordEvent(
            "monitoring",
            "started",
            "success",
            $"threshold_c={FormatDoubleForEvidence(_settings.ThresholdC)}; " +
            $"recovery_c={FormatDoubleForEvidence(_settings.RecoveryThresholdC)}; " +
            $"recovery_stable_s={_settings.RecoveryStableSeconds}; " +
            $"auto_reset_enabled={_settings.AutoResetEnabled}; " +
            $"poll_interval_ms={_settings.PollIntervalMs}");
        _monitoringTask = Task.Run(() => RunMonitoringLoopAsync(cancellationToken));
    }

    private async Task<bool> RebuildOrRestartMonitoringAsync(
        bool ensureG2000Connected = true,
        bool restartMonitoring = false,
        bool configurationAlreadyValidated = false)
    {
        if (_shutdownPreparing)
        {
            return false;
        }

        var shouldRestartMonitoring = restartMonitoring || _monitoringCts is not null;
        if (!shouldRestartMonitoring)
        {
            await RebuildServicesPreservingG2000Async(ensureG2000Connected);
            return true;
        }

        if (_monitoringCts is not null)
        {
            var previousMonitoringCts = _monitoringCts;
            var previousMonitoringTask = _monitoringTask;
            _monitoringCts = null;
            _monitoringTask = null;
            previousMonitoringCts.Cancel();
            previousMonitoringCts.Dispose();
            await AwaitMonitoringTaskAsync(previousMonitoringTask, CancellationToken.None);
            if (_shutdownPreparing || _monitoringCts is not null)
            {
                return false;
            }
        }

        if (!configurationAlreadyValidated && !ValidateMonitoringSetup())
        {
            _experimentRecorder?.RecordEvent(
                "settings",
                "apply-monitoring",
                "blocked",
                "Monitoring was stopped because the saved setup did not pass validation.");
            return false;
        }

        try
        {
            await RebuildServicesPreservingG2000Async(ensureG2000Connected);
        }
        catch
        {
            if (!_shutdownPreparing && _monitoringCts is null && _monitoringService is not null)
            {
                _experimentRecorder?.RecordEvent(
                    "monitoring",
                    "restart-after-settings-apply-failure",
                    "restarted",
                    "Monitoring resumed so an unconfirmed latched trip can retry the interlock stop.");
                StartMonitoringLoop(T("footer.monitoringRestarted"));
            }

            throw;
        }

        if (_shutdownPreparing || _monitoringCts is not null)
        {
            return false;
        }

        _monitoringCts = new CancellationTokenSource();
        var cancellationToken = _monitoringCts.Token;
        MarkMonitoringSessionStarted();
        SetStatus(_stateMachine.IsTripped ? MonitorStatus.Tripped : MonitorStatus.Monitoring);
        SetCurrentMode(T("mode.monitoring"));
        FooterText.Text = T("footer.monitoringRestarted");
        _experimentRecorder?.RecordEvent(
            "monitoring",
            "restarted",
            "success",
            $"threshold_c={FormatDoubleForEvidence(_settings.ThresholdC)}; " +
            $"recovery_c={FormatDoubleForEvidence(_settings.RecoveryThresholdC)}; " +
            $"recovery_stable_s={_settings.RecoveryStableSeconds}; " +
            $"auto_reset_enabled={_settings.AutoResetEnabled}; " +
            $"poll_interval_ms={_settings.PollIntervalMs}");
        _monitoringTask = Task.Run(() => RunMonitoringLoopAsync(cancellationToken));
        await RefreshGasFlowAsync(force: true);
        return true;
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
        _experimentRecorder?.RecordEvent("application", "error", "error", ex.Message);
        FooterText.Text = ex.Message;
        MessageBox.Show(this, ex.Message, T("app.title"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ShowSetupWarning(string message)
    {
        FooterText.Text = message;
        MessageBox.Show(this, message, T("message.setupRequiredTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string FormatDoubleForEvidence(double? value)
    {
        return value?.ToString("G17", CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string FormatOptionalDoubleForEvidence(double? value)
    {
        return value is null ? "unavailable" : FormatDoubleForEvidence(value);
    }

    private static string FormatG2000SetpointsForEvidence(G2000WritableSetpoints setpoints)
    {
        return $"voltage_v={FormatDoubleForEvidence(setpoints.VoltageV)}; " +
               $"frequency_khz={FormatDoubleForEvidence(setpoints.FrequencyKhz)}; " +
               $"duty_percent={FormatDoubleForEvidence(setpoints.DutyPercent)}; " +
               $"ton_ms={FormatDoubleForEvidence(setpoints.TonMs)}; " +
               $"toff_ms={FormatDoubleForEvidence(setpoints.ToffMs)}";
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
