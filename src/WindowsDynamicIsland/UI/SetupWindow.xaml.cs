using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WindowsDynamicIsland.Services;

namespace WindowsDynamicIsland.UI;

public partial class SetupWindow : Window
{
    private readonly bool _isUninstallMode;
    private bool _isBusy;

    public SetupWindow(bool isUninstallMode = false)
    {
        InitializeComponent();
        _isUninstallMode = isUninstallMode;
        InstallPathText.Text = $"Lokasi: {AppInstallerService.DefaultInstallDir}";

        if (_isUninstallMode)
        {
            ConfigureForUninstallMode();
        }
        else if (AppInstallerService.IsInstalled())
        {
            MainHeadingText.Text = "Perbarui atau Kelola Instalasi";
            SubHeadingText.Text = "Windows Dynamic Island sudah terpasang di perangkat ini. Anda dapat memperbarui instalasi atau meng-uninstall aplikasi.";
            PrimaryActionText.Text = "Perbarui / Reinstall";
            SecondaryUninstallBtn.Visibility = Visibility.Visible;
            AutostartCheck.IsChecked = AppInstallerService.IsAutostartEnabled();
        }
    }

    private void ConfigureForUninstallMode()
    {
        Title = "Uninstall Windows Dynamic Island";
        HeaderTitleText.Text = "Windows Dynamic Island Uninstaller";
        MainHeadingText.Text = "Uninstall Windows Dynamic Island?";
        SubHeadingText.Text = "Tindakan ini akan menghentikan Dynamic Island, menghapus shortcut Desktop dan Start Menu, menghapus entri Startup, serta membersihkan seluruh file aplikasi dari sistem Anda.";
        OptionsPanel.Visibility = Visibility.Collapsed;
        SecondaryUninstallBtn.Visibility = Visibility.Collapsed;

        PrimaryActionBtn.Background = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        PrimaryActionBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));
        PrimaryActionText.Text = "Uninstall Sekarang";
        PrimaryActionText.Foreground = new SolidColorBrush(Color.FromRgb(255, 255, 255));
    }

    private void OnTitleBarDragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        Close();
    }

    private async void OnPrimaryActionClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        if (_isUninstallMode)
        {
            await RunUninstallAsync();
        }
        else
        {
            await RunInstallAsync();
        }
    }

    private async void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        await RunUninstallAsync();
    }

    private async Task RunInstallAsync()
    {
        _isBusy = true;
        PrimaryActionBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        SecondaryUninstallBtn.IsEnabled = false;
        StatusProgressText.Visibility = Visibility.Visible;

        bool desktop = DesktopShortcutCheck.IsChecked == true;
        bool startMenu = StartMenuShortcutCheck.IsChecked == true;
        bool autostart = AutostartCheck.IsChecked == true;

        try
        {
            await Task.Run(() =>
            {
                AppInstallerService.InstallApplication(
                    createDesktopShortcut: desktop,
                    createStartMenuShortcut: startMenu,
                    enableAutostart: autostart,
                    launchAfterInstall: true,
                    progressCallback: msg => Dispatcher.InvokeAsync(() => StatusProgressText.Text = msg));
            });

            StatusProgressText.Text = "Instalasi berhasil! Menjalankan Windows Dynamic Island...";
            await Task.Delay(700);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _isBusy = false;
            PrimaryActionBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
            StatusProgressText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            StatusProgressText.Text = $"Gagal menginstal: {ex.Message}";
        }
    }

    private async Task RunUninstallAsync()
    {
        _isBusy = true;
        PrimaryActionBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        SecondaryUninstallBtn.IsEnabled = false;
        StatusProgressText.Visibility = Visibility.Visible;

        try
        {
            await Task.Run(() =>
            {
                AppInstallerService.UninstallApplication(
                    progressCallback: msg => Dispatcher.InvokeAsync(() => StatusProgressText.Text = msg));
            });

            StatusProgressText.Foreground = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            StatusProgressText.Text = "Windows Dynamic Island berhasil di-uninstall sepenuhnya.";
            await Task.Delay(900);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _isBusy = false;
            PrimaryActionBtn.IsEnabled = true;
            CancelBtn.IsEnabled = true;
            StatusProgressText.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113));
            StatusProgressText.Text = $"Gagal meng-uninstall: {ex.Message}";
        }
    }
}
