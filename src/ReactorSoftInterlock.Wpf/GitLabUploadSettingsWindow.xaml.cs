using System.Globalization;
using System.Windows;
using ReactorSoftInterlock.Infrastructure.Logging;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Wpf;

public partial class GitLabUploadSettingsWindow : Window
{
    private readonly string _language;
    private readonly IExperimentCredentialStore _credentialStore;

    public GitLabUploadSettingsWindow(
        ExperimentUploadSettings settings,
        string language,
        IExperimentCredentialStore credentialStore)
    {
        InitializeComponent();
        _language = language;
        _credentialStore = credentialStore;
        ResultSettings = Clone(settings);
        BindSettings();
        ApplyLanguage();
        Loaded += async (_, _) => await RefreshCredentialStatusAsync();
    }

    public ExperimentUploadSettings ResultSettings { get; private set; }

    public bool CredentialAvailable { get; private set; }

    private void BindSettings()
    {
        AutoUploadBox.IsChecked = ResultSettings.AutoUploadEnabled;
        BaseUrlBox.Text = ResultSettings.BaseUrl;
        ProjectIdBox.Text = ResultSettings.ProjectId.ToString(CultureInfo.InvariantCulture);
        PackageNameBox.Text = ResultSettings.PackageName;
        CredentialTargetBox.Text = ResultSettings.CredentialTarget;
    }

    private void ApplyLanguage()
    {
        Title = T("upload.settings.title");
        TitleText.Text = T("upload.settings.title");
        DescriptionText.Text = T("upload.settings.description");
        AutoUploadBox.Content = T("upload.settings.auto");
        BaseUrlLabel.Text = T("upload.settings.baseUrl");
        ProjectIdLabel.Text = T("upload.settings.projectId");
        PackageNameLabel.Text = T("upload.settings.packageName");
        CredentialTargetLabel.Text = T("upload.settings.credentialTarget");
        TokenLabel.Text = T("upload.settings.token");
        TokenHintText.Text = T("upload.settings.tokenHint");
        StoreCredentialButton.Content = T("upload.settings.store");
        ClearCredentialButton.Content = T("upload.settings.clear");
        CancelButton.Content = T("button.cancel");
        SaveButton.Content = T("button.save");
    }

    private async void StoreCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadSettingsFromControls();
            if (string.IsNullOrWhiteSpace(TokenBox.Password))
            {
                throw new InvalidOperationException(T("upload.settings.tokenRequired"));
            }

            await _credentialStore.StoreTokenAsync(
                ResultSettings.CredentialTarget,
                TokenBox.Password,
                CancellationToken.None);
            TokenBox.Clear();
            CredentialAvailable = true;
            UpdateCredentialStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("upload.settings.title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ClearCredentialButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadSettingsFromControls();
            await _credentialStore.ClearAsync(ResultSettings.CredentialTarget, CancellationToken.None);
            TokenBox.Clear();
            CredentialAvailable = false;
            UpdateCredentialStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("upload.settings.title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReadSettingsFromControls();
            await RefreshCredentialStatusAsync();
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, T("upload.settings.title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ReadSettingsFromControls()
    {
        if (!int.TryParse(ProjectIdBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var projectId) || projectId <= 0)
        {
            throw new InvalidOperationException(T("upload.settings.invalidProjectId"));
        }

        if (!ExperimentUploadSettings.TryGetSecureBaseUri(BaseUrlBox.Text, out _))
        {
            throw new InvalidOperationException(T("upload.settings.invalidUrl"));
        }

        ResultSettings = new ExperimentUploadSettings
        {
            AutoUploadEnabled = AutoUploadBox.IsChecked == true,
            BaseUrl = BaseUrlBox.Text.Trim(),
            ProjectId = projectId,
            PackageName = PackageNameBox.Text.Trim(),
            HttpTimeoutSeconds = ResultSettings.HttpTimeoutSeconds
        };
        ResultSettings.Normalize();
        CredentialTargetBox.Text = ResultSettings.CredentialTarget;
    }

    private async Task RefreshCredentialStatusAsync()
    {
        try
        {
            var token = await _credentialStore.ReadTokenAsync(
                ResultSettings.CredentialTarget, CancellationToken.None);
            CredentialAvailable = !string.IsNullOrWhiteSpace(token);
        }
        catch
        {
            CredentialAvailable = false;
        }

        UpdateCredentialStatus();
    }

    private void UpdateCredentialStatus()
    {
        CredentialStatusText.Text = CredentialAvailable
            ? T("upload.settings.credentialStored")
            : T("upload.settings.credentialMissing");
        CredentialStatusText.Foreground = CredentialAvailable
            ? System.Windows.Media.Brushes.ForestGreen
            : System.Windows.Media.Brushes.DarkOrange;
    }

    private string T(string key) => UiText.Get(_language, key);

    private static ExperimentUploadSettings Clone(ExperimentUploadSettings source) => new()
    {
        AutoUploadEnabled = source.AutoUploadEnabled,
        BaseUrl = source.BaseUrl,
        ProjectId = source.ProjectId,
        PackageName = source.PackageName,
        CredentialTarget = source.CredentialTarget,
        HttpTimeoutSeconds = source.HttpTimeoutSeconds
    };
}
