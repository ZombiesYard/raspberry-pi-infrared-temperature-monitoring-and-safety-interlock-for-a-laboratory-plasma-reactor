using System.Globalization;
using System.Windows;
using ReactorSoftInterlock.Infrastructure.Settings;

namespace ReactorSoftInterlock.Wpf;

public partial class AdvancedSettingsWindow : Window
{
    private readonly string _language;
    private readonly RelaySettings _workingCopy;

    public AdvancedSettingsWindow(RelaySettings settings, string language)
    {
        InitializeComponent();
        _language = language;
        _workingCopy = Clone(settings);
        _workingCopy.Normalize();
        ApplyLanguage();
        BindToUi();
    }

    public RelaySettings ResultSettings { get; private set; } = new();

    private void ApplyLanguage()
    {
        Title = T("advanced.title");
        DialogTitleText.Text = T("advanced.title");
        DialogSubtitleText.Text = T("advanced.subtitle");
        BaudLabel.Text = T("label.baud");
        AdvancedHintTitle.Text = T("advanced.scopeTitle");
        AdvancedHintText.Text = T("advanced.scopeBody");
        DefaultsTitleText.Text = T("advanced.defaultsTitle");
        DefaultsBodyText.Text = T("advanced.defaultsBody");
        RestoreDefaultsButton.Content = T("advanced.restoreDefaults");
        ChannelSectionTitle.Text = T("advanced.channelsTitle");
        HeaderChannelText.Text = T("advanced.header.channel");
        HeaderMappingText.Text = T("advanced.header.mapping");
        HeaderEnabledText.Text = T("advanced.header.enabled");
        HeaderOpenText.Text = T("advanced.header.open");
        HeaderCloseText.Text = T("advanced.header.close");
        FooterHintText.Text = T("advanced.footer");
        CancelButtonControl.Content = T("button.cancel");
        SaveButtonControl.Content = T("advanced.save");
        ApplyTooltips();
    }

    private void ApplyTooltips()
    {
        BaudBox.ToolTip = T("tooltip.advancedBaud");
        RestoreDefaultsButton.ToolTip = T("tooltip.advancedRestoreDefaults");
        Ch1EnabledBox.ToolTip = T("tooltip.advancedChannelEnabled");
        Ch2EnabledBox.ToolTip = T("tooltip.advancedChannelEnabled");
        Ch3EnabledBox.ToolTip = T("tooltip.advancedChannelEnabled");
        Ch4EnabledBox.ToolTip = T("tooltip.advancedChannelEnabled");
        Ch1OpenBox.ToolTip = T("tooltip.advancedOpenCommand");
        Ch2OpenBox.ToolTip = T("tooltip.advancedOpenCommand");
        Ch3OpenBox.ToolTip = T("tooltip.advancedOpenCommand");
        Ch4OpenBox.ToolTip = T("tooltip.advancedOpenCommand");
        Ch1CloseBox.ToolTip = T("tooltip.advancedCloseCommand");
        Ch2CloseBox.ToolTip = T("tooltip.advancedCloseCommand");
        Ch3CloseBox.ToolTip = T("tooltip.advancedCloseCommand");
        Ch4CloseBox.ToolTip = T("tooltip.advancedCloseCommand");
        CancelButtonControl.ToolTip = T("tooltip.advancedCancel");
        SaveButtonControl.ToolTip = T("tooltip.advancedSave");
    }

    private void BindToUi()
    {
        BaudBox.Text = _workingCopy.BaudRate.ToString(CultureInfo.InvariantCulture);
        BindChannel(_workingCopy.Channels[0], Ch1EnabledBox, Ch1OpenBox, Ch1CloseBox, Ch1MappingText);
        BindChannel(_workingCopy.Channels[1], Ch2EnabledBox, Ch2OpenBox, Ch2CloseBox, Ch2MappingText);
        BindChannel(_workingCopy.Channels[2], Ch3EnabledBox, Ch3OpenBox, Ch3CloseBox, Ch3MappingText);
        BindChannel(_workingCopy.Channels[3], Ch4EnabledBox, Ch4OpenBox, Ch4CloseBox, Ch4MappingText);
    }

    private static void BindChannel(
        RelayChannelSettings channel,
        System.Windows.Controls.CheckBox enabledBox,
        System.Windows.Controls.TextBox openBox,
        System.Windows.Controls.TextBox closeBox,
        System.Windows.Controls.TextBlock mappingText)
    {
        enabledBox.IsChecked = channel.Enabled;
        openBox.Text = channel.OpenCommand;
        closeBox.Text = channel.CloseCommand;
        mappingText.Text = channel.InterlockMapping;
    }

    private void RestoreDefaultsButton_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new RelaySettings();
        defaults.Normalize();
        _workingCopy.BaudRate = defaults.BaudRate;
        _workingCopy.Channels = defaults.Channels.Select(Clone).ToList();
        BindToUi();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(BaudBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var baudRate) || baudRate <= 0)
        {
            MessageBox.Show(this, T("advanced.invalidBaud"), T("advanced.title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _workingCopy.BaudRate = baudRate;
        ApplyChannelFromUi(_workingCopy.Channels[0], Ch1EnabledBox, Ch1OpenBox, Ch1CloseBox);
        ApplyChannelFromUi(_workingCopy.Channels[1], Ch2EnabledBox, Ch2OpenBox, Ch2CloseBox);
        ApplyChannelFromUi(_workingCopy.Channels[2], Ch3EnabledBox, Ch3OpenBox, Ch3CloseBox);
        ApplyChannelFromUi(_workingCopy.Channels[3], Ch4EnabledBox, Ch4OpenBox, Ch4CloseBox);
        _workingCopy.Normalize();
        ResultSettings = Clone(_workingCopy);
        DialogResult = true;
    }

    private static void ApplyChannelFromUi(
        RelayChannelSettings channel,
        System.Windows.Controls.CheckBox enabledBox,
        System.Windows.Controls.TextBox openBox,
        System.Windows.Controls.TextBox closeBox)
    {
        channel.Enabled = enabledBox.IsChecked == true;
        channel.OpenCommand = openBox.Text.Trim();
        channel.CloseCommand = closeBox.Text.Trim();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private string T(string key)
    {
        return UiText.Get(_language, key);
    }

    private static RelaySettings Clone(RelaySettings source)
    {
        return new RelaySettings
        {
            Mode = source.Mode,
            DryRun = source.DryRun,
            PortName = source.PortName,
            BaudRate = source.BaudRate,
            StopCommandHex = source.StopCommandHex,
            ResetCommandHex = source.ResetCommandHex,
            OpenOnAlarm = source.OpenOnAlarm,
            G2000Can = source.G2000Can.Clone(),
            Channels = source.Channels.Select(Clone).ToList()
        };
    }

    private static RelayChannelSettings Clone(RelayChannelSettings source)
    {
        return new RelayChannelSettings
        {
            ChannelNumber = source.ChannelNumber,
            DisplayName = source.DisplayName,
            InterlockMapping = source.InterlockMapping,
            Enabled = source.Enabled,
            OpenCommand = source.OpenCommand,
            CloseCommand = source.CloseCommand
        };
    }
}
