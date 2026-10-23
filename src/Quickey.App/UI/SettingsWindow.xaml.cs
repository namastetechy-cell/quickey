using System.Reflection;
using System.Windows;
using Quickey.App.Services;
using Quickey.App.Shell;
using Quickey.Core.Licensing;
using Quickey.Core.Settings;

namespace Quickey.App.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _save;
    private readonly LicenseManager _license;
    private readonly UsageTracker _usage;
    private readonly bool _testingBuild;

    public SettingsWindow(AppSettings settings, Action<AppSettings> save, LicenseManager license, UsageTracker usage, bool testingBuild)
    {
        _testingBuild = testingBuild;
        InitializeComponent();
        _settings = settings;
        _save = save;
        _license = license;
        _usage = usage;

        foreach (var box in new[] { ConvertKey, ToolsKey, ActionsKey })
            box.ItemsSource = Enum.GetValues<ModifierKey>();
        ConvertKey.SelectedItem = settings.ConvertKey;
        ToolsKey.SelectedItem = settings.ToolsKey;
        ActionsKey.SelectedItem = settings.ActionsKey;
        ExplorerOnly.IsChecked = settings.ExplorerOnly;
        Subfolder.IsChecked = settings.OutputLocation == OutputLocation.QuickeySubfolder;
        Notifications.IsChecked = settings.ShowNotifications;
        StartWithWindows.IsChecked = ShellIntegration.StartsWithWindows;
        ContextMenuOption.IsChecked = ShellIntegration.IsContextMenuRegistered;

        Version.Text = $"Quickey {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        _license.Changed += OnLicenseChanged;
        Closed += (_, _) => _license.Changed -= OnLicenseChanged;
        RefreshLicense();
    }

    private void OnLicenseChanged(object? sender, EventArgs e) => Dispatcher.Invoke(RefreshLicense);

    private void RefreshLicense()
    {
        var record = _license.Current;
        var pro = _license.IsPro;
        if (_testingBuild)
        {
            LicenseStatus.Text = "Testing build: unlimited conversions. Licensing switches on once the store details are filled in.";
            ActivatePanel.Visibility = Deactivate.Visibility = GetPro.Visibility = Visibility.Collapsed;
            return;
        }
        LicenseStatus.Text = pro
            ? $"Quickey Pro is active on this PC{(record?.CustomerEmail is { } email ? $" ({email})" : "")}. Unlimited conversions."
            : $"Free version: {_usage.RemainingToday} of {UsageTracker.FreeDailyConversions} conversions left today. File actions are always free.";
        ActivatePanel.Visibility = pro ? Visibility.Collapsed : Visibility.Visible;
        Deactivate.Visibility = record is null ? Visibility.Collapsed : Visibility.Visible;
        GetPro.Visibility = !pro && Brand.CheckoutUrl != "" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnActivate(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LicenseKey.Text))
            return;
        ActivateButton.IsEnabled = false;
        try
        {
            var result = await _license.ActivateAsync(LicenseKey.Text, Environment.MachineName);
            if (result.Success)
                LicenseKey.Clear();
            else
                MessageBox.Show(this, result.Error, "Couldn't activate", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ActivateButton.IsEnabled = true;
        }
    }

    private async void OnDeactivate(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Remove the license from this PC? You can activate it again here or on another PC.",
                "Deactivate Quickey Pro", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await _license.DeactivateAsync();
    }

    private void OnGetPro(object sender, RoutedEventArgs e) => TrayIcon.OpenUrl(Brand.CheckoutUrl);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var updated = new AppSettings
        {
            ConvertKey = (ModifierKey)ConvertKey.SelectedItem,
            ToolsKey = (ModifierKey)ToolsKey.SelectedItem,
            ActionsKey = (ModifierKey)ActionsKey.SelectedItem,
            ExplorerOnly = ExplorerOnly.IsChecked == true,
            OutputLocation = Subfolder.IsChecked == true ? OutputLocation.QuickeySubfolder : OutputLocation.SameFolder,
            ShowNotifications = Notifications.IsChecked == true,
            StartWithWindows = StartWithWindows.IsChecked == true,
            UpdateUrl = _settings.UpdateUrl,
        };
        if (!updated.HasDistinctKeys)
        {
            KeyError.Visibility = Visibility.Visible;
            return;
        }

        ShellIntegration.StartsWithWindows = updated.StartWithWindows;
        if (ContextMenuOption.IsChecked == true)
            ShellIntegration.RegisterContextMenu();
        else
            ShellIntegration.UnregisterContextMenu();

        _save(updated);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
