using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WindowsDynamicIsland.Services;

namespace WindowsDynamicIsland.UI
{
    public enum SetupWindowMode
    {
        Install,
        Update,
        ApplyStagedUpdate,
        Uninstall
    }

    public partial class SetupWindow : Window
    {
        private SetupWindowMode _mode;
        private readonly UpdateCheckerService? _updateService;
        private readonly string _targetInstallDir;
        private readonly int _waitPid;
        private bool _isWorking;
        private bool _isCompleted;
        private bool _isEnglish;

        public SetupWindow(bool isUninstallMode = false)
            : this(isUninstallMode ? SetupWindowMode.Uninstall : SetupWindowMode.Install)
        {
        }

        public SetupWindow(
            SetupWindowMode mode,
            UpdateCheckerService? updateService = null,
            string? targetInstallDir = null,
            int waitPid = 0)
        {
            InitializeComponent();

            _mode = mode;
            _updateService = updateService;
            _targetInstallDir = string.IsNullOrWhiteSpace(targetInstallDir)
                ? AppInstallerService.DefaultInstallDir
                : targetInstallDir;
            _waitPid = waitPid;
            _isEnglish = LocalizationService.Instance.IsEnglish;

            ConfigureInitialState();

            Loaded += async (_, _) =>
            {
                if (_mode == SetupWindowMode.ApplyStagedUpdate)
                {
                    await ExecuteApplyStagedUpdateAsync();
                }
                else if (_mode == SetupWindowMode.Update && _updateService != null && !_updateService.LastResult.IsUpdateAvailable)
                {
                    await RefreshUpdateInfoAsync();
                }
            };
        }

        private static string GetCurrentAppVersion() => AppInstallerService.AppVersion;

        private void ConfigureInitialState()
        {
            string currentVer = GetCurrentAppVersion();
            VersionBadgeText.Text = $"v{currentVer}";
            TargetInstallDirText.Text = _targetInstallDir;
            LangToggleBtn.Content = _isEnglish ? "EN" : "ID";

            SetStepIndicator(1);
            ApplyLocalization();
        }

        private void SetStepIndicator(int activeStep)
        {
            var activeBg = _mode == SetupWindowMode.Uninstall
                ? BrushCache.GetHex("#FB7185")
                : BrushCache.GetHex("#38BDF8");
            var doneBg = BrushCache.GetHex("#22C55E");
            var idleBg = BrushCache.GetHex("#1EFFFFFF");

            var activeText = BrushCache.GetHex("#F8FAFC");
            var doneText = BrushCache.GetHex("#34D399");
            var idleText = BrushCache.GetHex("#64748B");

            Step1Badge.Background = activeStep == 1 ? activeBg : (activeStep > 1 ? doneBg : idleBg);
            Step1Text.Foreground = activeStep == 1 ? activeText : (activeStep > 1 ? doneText : idleText);

            Step2Badge.Background = activeStep == 2 ? activeBg : (activeStep > 2 ? doneBg : idleBg);
            Step2Text.Foreground = activeStep == 2 ? activeText : (activeStep > 2 ? doneText : idleText);

            Step3Badge.Background = activeStep == 3 ? doneBg : idleBg;
            Step3Text.Foreground = activeStep == 3 ? activeText : idleText;
        }

        private void ApplyLocalization()
        {
            LangToggleBtn.Content = _isEnglish ? "EN" : "ID";

            Step1Text.Text = _isEnglish ? "Configure" : "Konfigurasi";
            Step2Text.Text = _isEnglish ? "Processing" : "Eksekusi";
            Step3Text.Text = _isEnglish ? "Complete" : "Selesai";

            OptDesktopTitle.Text = _isEnglish ? "Desktop Shortcut" : "Pintasan Desktop (Desktop Shortcut)";
            OptDesktopSub.Text = _isEnglish
                ? "Place a quick-launch icon on your main Windows Desktop"
                : "Tambahkan ikon peluncur cepat di layar Desktop utama";

            OptStartMenuTitle.Text = _isEnglish ? "Start Menu & Windows Search" : "Integrasi Start Menu & Windows Search";
            OptStartMenuSub.Text = _isEnglish
                ? "Register in All Apps and Windows Search index"
                : "Daftarkan ke daftar semua aplikasi dan pencarian Windows";

            OptStartupTitle.Text = _isEnglish ? "Launch Automatically at Windows Sign-In" : "Jalankan Otomatis Saat Windows Menyala";
            OptStartupSub.Text = _isEnglish
                ? "Start Dynamic Island seamlessly every time you log in"
                : "Aktifkan Dynamic Island secara instan setiap kali masuk ke Windows";

            InstallPathLabelText.Text = _isEnglish ? "System Installation Directory" : "Lokasi Instalasi Sistem";

            UpdateNotesHeaderText.Text = _isEnglish ? "Release Highlights & Improvements" : "Catatan Pembaruan & Peningkatan";
            UpdateSecurityNoteText.Text = _isEnglish
                ? "Release package is verified with SHA-256 and applied natively with zero external scripts."
                : "Paket rilis diverifikasi dengan SHA-256 dan diterapkan secara langsung tanpa skrip eksternal.";

            UninstallInfoTitleText.Text = _isEnglish ? "Components Scheduled for Removal" : "Komponen yang Akan Dihapus";
            UninstallInfoDescText.Text = _isEnglish
                ? "All application binaries, Desktop and Start Menu shortcuts, battery reminder tasks, startup entries, and Windows Installed Apps registrations will be cleanly removed."
                : "Seluruh biner aplikasi, pintasan Desktop/Start Menu, jadwal pengingat baterai, entri Auto-Start, serta registrasi Windows Installed Apps akan dibersihkan secara menyeluruh.";

            OptRemoveDataTitle.Text = _isEnglish ? "Also Delete User Settings & Local Cache" : "Hapus Juga Pengaturan & Data Pengguna";
            OptRemoveDataSub.Text = _isEnglish
                ? "Remove saved preferences, calendar schedule, and cache in AppData"
                : "Bersihkan konfigurasi, jadwal kalender lokal, dan cache di AppData";

            if (!_isWorking && !_isCompleted)
            {
                CancelBtn.Content = _isEnglish ? "Cancel" : "Batal";
                ConfigureModePanelsAndLabels();
            }
            else if (_isCompleted)
            {
                PrimaryActionBtn.Content = _isEnglish ? "Done" : "Selesai";
            }

            ConfigurePhaseChecklistLabels();
        }

        private void ConfigurePhaseChecklistLabels()
        {
            if (_mode == SetupWindowMode.Update || _mode == SetupWindowMode.ApplyStagedUpdate)
            {
                Phase1Text.Text = _isEnglish ? "Connect to release server & download package" : "Mengunduh paket pembaruan resmi dari server";
                Phase2Text.Text = _isEnglish ? "Verify SHA-256 cryptographic signature" : "Memverifikasi tanda tangan kriptografi SHA-256";
                Phase3Text.Text = _isEnglish ? "Extract & stage new application binaries" : "Mengekstrak & menyiapkan biner versi terbaru";
                Phase4Text.Text = _isEnglish ? "Replace binaries & restart Dynamic Island" : "Memperbarui sistem & menjalankan ulang aplikasi";
            }
            else if (_mode == SetupWindowMode.Uninstall)
            {
                Phase1Text.Text = _isEnglish ? "Stop active Windows Dynamic Island processes" : "Menghentikan proses aktif Windows Dynamic Island";
                Phase2Text.Text = _isEnglish ? "Remove startup entries, tasks & shortcuts" : "Menghapus pintasan, jadwal tugas & entri Startup";
                Phase3Text.Text = _isEnglish ? "Unregister from Windows Installed Apps" : "Menghapus registrasi dari Windows Installed Apps";
                Phase4Text.Text = _isEnglish ? "Clean installation directory & temporary cache" : "Membersihkan direktori instalasi & berkas sistem";
            }
            else
            {
                Phase1Text.Text = _isEnglish ? "Prepare workspace & stop active instances" : "Menyiapkan ruang kerja & menghentikan proses lama";
                Phase2Text.Text = _isEnglish ? "Copy & unblock core application binaries" : "Menyalin & memverifikasi integritas berkas inti";
                Phase3Text.Text = _isEnglish ? "Create native shortcuts & registry entries" : "Membuat pintasan sistem & registrasi Windows";
                Phase4Text.Text = _isEnglish ? "Finalize setup & launch Dynamic Island" : "Finalisasi instalasi & menjalankan Dynamic Island";
            }
        }

        private void ConfigureModePanelsAndLabels()
        {
            InstallConfigPanel.Visibility = Visibility.Collapsed;
            UpdateConfigPanel.Visibility = Visibility.Collapsed;
            UninstallConfigPanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Collapsed;
            CompletePanel.Visibility = Visibility.Collapsed;
            SecondaryActionBtn.Visibility = Visibility.Collapsed;

            string currentVer = GetCurrentAppVersion();

            if (_mode == SetupWindowMode.Uninstall)
            {
                UninstallConfigPanel.Visibility = Visibility.Visible;
                HeaderTitleText.Text = _isEnglish ? "Windows Dynamic Island Uninstaller" : "Penghapusan Windows Dynamic Island";
                MainHeadingText.Text = _isEnglish ? "Uninstall Windows Dynamic Island" : "Copot Windows Dynamic Island";
                SubHeadingText.Text = _isEnglish
                    ? "Cleanly remove the application and its system integrations from your PC"
                    : "Hapus aplikasi beserta seluruh integrasi sistem dari komputer Anda";

                HeroIconBorder.Background = BrushCache.GetHex("#18FB7185");
                HeroIconBorder.BorderBrush = BrushCache.GetHex("#40FB7185");
                HeroAppLogo.Visibility = Visibility.Collapsed;
                HeroModeVector.Data = TryFindResource("GeomTrash") as Geometry;
                HeroModeVector.Stroke = BrushCache.GetHex("#FB7185");
                HeroModeVector.Visibility = Visibility.Visible;

                HeroStatusPill.Visibility = Visibility.Visible;
                HeroStatusPill.Background = BrushCache.GetHex("#18FB7185");
                HeroStatusPill.BorderBrush = BrushCache.GetHex("#38FB7185");
                HeroStatusPillText.Foreground = BrushCache.GetHex("#FB7185");
                HeroStatusPillText.Text = $"v{AppInstallerService.GetInstalledVersion() ?? currentVer}";

                PrimaryActionBtn.Content = _isEnglish ? "Uninstall Now" : "Copot Sekarang";
                PrimaryActionBtn.Background = BrushCache.GetHex("#FB7185");
                ActionProgressBar.Foreground = BrushCache.GetHex("#FB7185");
            }
            else if (_mode == SetupWindowMode.Update || _mode == SetupWindowMode.ApplyStagedUpdate)
            {
                UpdateConfigPanel.Visibility = _mode == SetupWindowMode.Update ? Visibility.Visible : Visibility.Collapsed;
                ProgressPanel.Visibility = _mode == SetupWindowMode.ApplyStagedUpdate ? Visibility.Visible : Visibility.Collapsed;

                string latestVer = (_updateService?.LastResult.LatestVersion ?? currentVer).TrimStart('v', 'V');
                HeaderTitleText.Text = _isEnglish ? "Windows Dynamic Island Updater" : "Pembaruan Windows Dynamic Island";
                MainHeadingText.Text = _isEnglish
                    ? $"Update to Version v{latestVer}"
                    : $"Perbarui ke Versi v{latestVer}";
                SubHeadingText.Text = _isEnglish
                    ? "Seamless in-app upgrade with automatic restart and preserved user settings"
                    : "Pembaruan otomatis tanpa kehilangan pengaturan dan jadwal Anda";

                HeroIconBorder.Background = BrushCache.GetHex("#1838BDF8");
                HeroIconBorder.BorderBrush = BrushCache.GetHex("#4038BDF8");
                HeroAppLogo.Visibility = Visibility.Collapsed;
                HeroModeVector.Data = TryFindResource("GeomUpdate") as Geometry;
                HeroModeVector.Stroke = BrushCache.GetHex("#38BDF8");
                HeroModeVector.Visibility = Visibility.Visible;

                HeroStatusPill.Visibility = Visibility.Visible;
                HeroStatusPill.Background = BrushCache.GetHex("#1838BDF8");
                HeroStatusPill.BorderBrush = BrushCache.GetHex("#3838BDF8");
                HeroStatusPillText.Foreground = BrushCache.GetHex("#38BDF8");
                HeroStatusPillText.Text = $"v{currentVer} -> v{latestVer}";

                string notes = _updateService?.LastResult.ReleaseNotes ?? string.Empty;
                if (string.IsNullOrWhiteSpace(notes))
                {
                    ReleaseNotesBodyText.Text = _isEnglish
                        ? "- Performance and memory optimizations across all HUD modules\n- Native zero-PowerShell installer, updater, and uninstaller\n- Full Indonesian and English localization support\n- Improved Japanese/CJK romaji lyric spacing and layout stability"
                        : "- Optimasi performa dan efisiensi memori di seluruh modul HUD\n- Sistem instalasi, pembaruan, dan penghapusan native tanpa PowerShell\n- Dukungan penuh lokalisasi Bahasa Indonesia dan Bahasa Inggris\n- Perbaikan spasi lirik Romaji Jepang/CJK dan tata letak anti-tumpang tindih";
                }
                else
                {
                    ReleaseNotesBodyText.Text = notes.Trim();
                }

                PrimaryActionBtn.Content = _isEnglish ? "Download & Update" : "Unduh & Perbarui";
                PrimaryActionBtn.Background = BrushCache.GetHex("#38BDF8");
                ActionProgressBar.Foreground = BrushCache.GetHex("#38BDF8");

                SecondaryActionBtn.Content = _isEnglish ? "View on GitHub" : "Lihat di GitHub";
                SecondaryActionBtn.Visibility = Visibility.Visible;
            }
            else
            {
                InstallConfigPanel.Visibility = Visibility.Visible;
                bool alreadyInstalled = AppInstallerService.IsInstalled();
                string? installedVer = AppInstallerService.GetInstalledVersion();

                HeaderTitleText.Text = _isEnglish ? "Windows Dynamic Island Setup" : "Instalasi Windows Dynamic Island";
                MainHeadingText.Text = alreadyInstalled
                    ? (_isEnglish ? "Update or Repair Installation" : "Perbarui atau Perbaiki Instalasi")
                    : (_isEnglish ? "Install Windows Dynamic Island" : "Instal Windows Dynamic Island");
                SubHeadingText.Text = alreadyInstalled
                    ? (_isEnglish
                        ? $"Installed version v{installedVer ?? currentVer} detected. Ready to upgrade or repair."
                        : $"Terdeteksi versi v{installedVer ?? currentVer} terpasang. Siap diperbarui atau diperbaiki.")
                    : (_isEnglish
                        ? "Interactive macOS-inspired Dynamic Island experience for Windows 10 & 11"
                        : "Antarmuka interaktif bergaya macOS untuk Windows 10 dan 11");

                HeroIconBorder.Background = BrushCache.GetHex("#1638BDF8");
                HeroIconBorder.BorderBrush = BrushCache.GetHex("#3538BDF8");
                HeroAppLogo.Visibility = Visibility.Visible;
                HeroModeVector.Visibility = Visibility.Collapsed;

                HeroStatusPill.Visibility = Visibility.Visible;
                HeroStatusPill.Background = alreadyInstalled ? BrushCache.GetHex("#1834D399") : BrushCache.GetHex("#1838BDF8");
                HeroStatusPill.BorderBrush = alreadyInstalled ? BrushCache.GetHex("#3834D399") : BrushCache.GetHex("#3838BDF8");
                HeroStatusPillText.Foreground = alreadyInstalled ? BrushCache.GetHex("#34D399") : BrushCache.GetHex("#38BDF8");
                HeroStatusPillText.Text = alreadyInstalled
                    ? (_isEnglish ? "Installed" : "Terpasang")
                    : (_isEnglish ? "Ready" : "Siap");

                PrimaryActionBtn.Content = alreadyInstalled
                    ? (_isEnglish ? "Update / Repair" : "Perbarui / Perbaiki")
                    : (_isEnglish ? "Install Now" : "Instal Sekarang");
                PrimaryActionBtn.Background = BrushCache.GetHex("#38BDF8");
                ActionProgressBar.Foreground = BrushCache.GetHex("#38BDF8");

                if (alreadyInstalled)
                {
                    SecondaryActionBtn.Content = _isEnglish ? "Switch to Uninstall" : "Copot Pemasangan";
                    SecondaryActionBtn.Visibility = Visibility.Visible;
                }
            }
        }

        private async Task RefreshUpdateInfoAsync()
        {
            if (_updateService == null) return;
            try
            {
                ReleaseNotesBodyText.Text = _isEnglish
                    ? "Checking GitHub Releases for the latest version package..."
                    : "Memeriksa rilis terbaru dari repositori GitHub resmi...";
                await _updateService.CheckForUpdatesAsync(isBackgroundCheck: false);
                ConfigureModePanelsAndLabels();
            }
            catch
            {
                // Ignore network errors; fallback release URL remains ready
            }
        }

        private void UpdatePhaseChecklistVisuals(int percentage)
        {
            var activeBg = _mode == SetupWindowMode.Uninstall
                ? BrushCache.GetHex("#28FB7185")
                : BrushCache.GetHex("#2838BDF8");
            var doneBg = BrushCache.GetHex("#2422C55E");
            var idleBg = BrushCache.GetHex("#18FFFFFF");

            var activeStroke = _mode == SetupWindowMode.Uninstall
                ? BrushCache.GetHex("#FB7185")
                : BrushCache.GetHex("#38BDF8");
            var doneStroke = BrushCache.GetHex("#22C55E");
            var idleStroke = BrushCache.GetHex("#64748B");

            var activeFg = BrushCache.GetHex("#F8FAFC");
            var doneFg = BrushCache.GetHex("#CBD5E1");
            var idleFg = BrushCache.GetHex("#64748B");

            void SetPhase(System.Windows.Controls.Border badge, System.Windows.Shapes.Path icon, System.Windows.Controls.TextBlock label, int startPct, int donePct)
            {
                if (percentage >= donePct)
                {
                    badge.Background = doneBg;
                    icon.Stroke = doneStroke;
                    label.Foreground = doneFg;
                }
                else if (percentage >= startPct)
                {
                    badge.Background = activeBg;
                    icon.Stroke = activeStroke;
                    label.Foreground = activeFg;
                }
                else
                {
                    badge.Background = idleBg;
                    icon.Stroke = idleStroke;
                    label.Foreground = idleFg;
                }
            }

            SetPhase(Phase1Badge, Phase1Icon, Phase1Text, 1, 25);
            SetPhase(Phase2Badge, Phase2Icon, Phase2Text, 25, 60);
            SetPhase(Phase3Badge, Phase3Icon, Phase3Text, 60, 88);
            SetPhase(Phase4Badge, Phase4Icon, Phase4Text, 88, 100);
        }

        private void ReportProgressOnUi(int pct, string statusMessage)
        {
            Dispatcher.Invoke(() =>
            {
                int clamped = Math.Clamp(pct, 0, 100);
                ActionProgressBar.Value = clamped;
                ProgressPercentText.Text = $"{clamped}%";
                StatusProgressText.Text = statusMessage;
                UpdatePhaseChecklistVisuals(clamped);
            });
        }

        private void TransitionToProgressView()
        {
            _isWorking = true;
            SetStepIndicator(2);

            InstallConfigPanel.Visibility = Visibility.Collapsed;
            UpdateConfigPanel.Visibility = Visibility.Collapsed;
            UninstallConfigPanel.Visibility = Visibility.Collapsed;
            CompletePanel.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;

            PrimaryActionBtn.IsEnabled = false;
            CancelBtn.IsEnabled = false;
            SecondaryActionBtn.Visibility = Visibility.Collapsed;
            CloseBtn.IsEnabled = false;

            UpdatePhaseChecklistVisuals(2);
        }

        private void TransitionToCompleteView(string title, string subtitle, bool isError = false)
        {
            _isWorking = false;
            _isCompleted = !isError;
            SetStepIndicator(isError ? 2 : 3);

            ProgressPanel.Visibility = Visibility.Collapsed;
            CompletePanel.Visibility = Visibility.Visible;

            if (isError)
            {
                CompleteIconBadge.Background = BrushCache.GetHex("#18FB7185");
                CompleteIconBadge.BorderBrush = BrushCache.GetHex("#40FB7185");
                CompleteIconPath.Data = TryFindResource("GeomClosePower") as Geometry;
                CompleteIconPath.Stroke = BrushCache.GetHex("#FB7185");
                PrimaryActionBtn.Background = BrushCache.GetHex("#38BDF8");
                PrimaryActionBtn.Content = _isEnglish ? "Retry" : "Coba Lagi";
                CancelBtn.IsEnabled = true;
                CancelBtn.Visibility = Visibility.Visible;
                _isCompleted = false;
            }
            else
            {
                CompleteIconBadge.Background = BrushCache.GetHex("#1822C55E");
                CompleteIconBadge.BorderBrush = BrushCache.GetHex("#4022C55E");
                CompleteIconPath.Data = TryFindResource("GeomCheckCircle") as Geometry;
                CompleteIconPath.Stroke = BrushCache.GetHex("#22C55E");
                PrimaryActionBtn.Background = BrushCache.GetHex("#22C55E");
                PrimaryActionBtn.Content = _isEnglish ? "Done" : "Selesai";
                CancelBtn.Visibility = Visibility.Collapsed;
            }

            CompleteTitleText.Text = title;
            CompleteSubText.Text = subtitle;
            PrimaryActionBtn.IsEnabled = true;
            CloseBtn.IsEnabled = true;
        }

        private async void OnPrimaryActionClick(object sender, RoutedEventArgs e)
        {
            if (_isWorking) return;

            if (_isCompleted)
            {
                CloseOrShutdown();
                return;
            }

            if (_mode == SetupWindowMode.Uninstall)
            {
                await ExecuteUninstallAsync();
            }
            else if (_mode == SetupWindowMode.Update)
            {
                await ExecuteDownloadAndStageUpdateAsync();
            }
            else if (_mode == SetupWindowMode.ApplyStagedUpdate)
            {
                await ExecuteApplyStagedUpdateAsync();
            }
            else
            {
                await ExecuteInstallAsync();
            }
        }

        private async Task ExecuteInstallAsync()
        {
            TransitionToProgressView();
            PrimaryActionBtn.Content = _isEnglish ? "Installing..." : "Menginstal...";

            bool createDesktop = DesktopShortcutCheck.IsChecked == true;
            bool createStartMenu = StartMenuShortcutCheck.IsChecked == true;
            bool autoStart = AutoStartCheck.IsChecked == true;

            bool success = await Task.Run(() =>
            {
                try
                {
                    if (_waitPid > 0)
                    {
                        AppInstallerService.WaitForProcessExit(_waitPid, 5000);
                    }

                    AppInstallerService.InstallApplication(
                        enableAutostart: autoStart,
                        createDesktopShortcut: createDesktop,
                        createStartMenuShortcut: createStartMenu,
                        launchAfterInstall: true,
                        progressCallback: null,
                        detailedProgressCallback: ReportProgressOnUi);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.LogError("SetupWindow.ExecuteInstall", ex.Message, ex);
                    return false;
                }
            });

            if (success)
            {
                TransitionToCompleteView(
                    _isEnglish ? "Installation Completed Successfully" : "Instalasi Berhasil Diselesaikan",
                    _isEnglish
                        ? "Windows Dynamic Island is now installed and running at the top of your screen."
                        : "Windows Dynamic Island telah terpasang dan langsung aktif di bagian atas layar Anda.");
            }
            else
            {
                TransitionToCompleteView(
                    _isEnglish ? "Installation Could Not Be Completed" : "Instalasi Gagal Diselesaikan",
                    _isEnglish
                        ? "Please ensure no external security software is locking the application folder and try again."
                        : "Pastikan tidak ada program keamanan yang mengunci folder aplikasi lalu coba kembali.",
                    isError: true);
            }
        }

        private async Task ExecuteUninstallAsync()
        {
            TransitionToProgressView();
            PrimaryActionBtn.Content = _isEnglish ? "Uninstalling..." : "Menghapus...";

            bool removeUserData = RemoveUserDataCheck.IsChecked == true;

            bool success = await Task.Run(() =>
            {
                try
                {
                    if (_waitPid > 0)
                    {
                        AppInstallerService.WaitForProcessExit(_waitPid, 6000);
                    }

                    AppInstallerService.UninstallApplication(
                        progressCallback: null,
                        removeUserData: removeUserData,
                        detailedProgressCallback: ReportProgressOnUi);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.LogError("SetupWindow.ExecuteUninstall", ex.Message, ex);
                    return false;
                }
            });

            if (success)
            {
                TransitionToCompleteView(
                    _isEnglish ? "Uninstallation Completed" : "Pencopotan Berhasil Diselesaikan",
                    _isEnglish
                        ? "Windows Dynamic Island and all selected components have been cleanly removed from your PC."
                        : "Windows Dynamic Island beserta seluruh komponen yang dipilih telah dibersihkan dari komputer Anda.");
            }
            else
            {
                TransitionToCompleteView(
                    _isEnglish ? "Uninstallation Encountered an Issue" : "Pencopotan Mengalami Kendala",
                    _isEnglish
                        ? "Some files could not be removed. Please close any open folders and try again."
                        : "Beberapa berkas tidak dapat dihapus. Tutup jendela yang sedang membuka folder aplikasi lalu coba lagi.",
                    isError: true);
            }
        }

        private async Task ExecuteDownloadAndStageUpdateAsync()
        {
            TransitionToProgressView();
            PrimaryActionBtn.Content = _isEnglish ? "Updating..." : "Memperbarui...";

            var service = _updateService ?? new UpdateCheckerService();
            bool readyToRestart = await service.DownloadAndInstallUpdateAsync(ReportProgressOnUi);

            if (readyToRestart)
            {
                ReportProgressOnUi(100, _isEnglish
                    ? "Restarting into updated version..."
                    : "Menjalankan ulang ke versi terbaru...");
                await Task.Delay(350);
                Application.Current.Shutdown();
            }
            else
            {
                TransitionToCompleteView(
                    _isEnglish ? "Update Download Failed" : "Pengunduhan Pembaruan Gagal",
                    _isEnglish
                        ? "Could not download or verify the update package. Please check your internet connection or view the release on GitHub."
                        : "Gagal mengunduh atau memverifikasi paket pembaruan. Periksa koneksi internet Anda atau unduh melalui halaman GitHub.",
                    isError: true);
            }
        }

        private async Task ExecuteApplyStagedUpdateAsync()
        {
            TransitionToProgressView();
            PrimaryActionBtn.Content = _isEnglish ? "Applying Update..." : "Menerapkan Pembaruan...";

            bool applied = await Task.Run(() =>
            {
                try
                {
                    AppInstallerService.ApplyStagedUpdate(
                        _targetInstallDir,
                        _waitPid,
                        ReportProgressOnUi);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.LogError("SetupWindow.ExecuteApplyStagedUpdate", ex.Message, ex);
                    return false;
                }
            });

            if (applied)
            {
                await Task.Delay(300);
                Application.Current.Shutdown();
            }
            else
            {
                TransitionToCompleteView(
                    _isEnglish ? "Failed to Apply Staged Update" : "Gagal Menerapkan Pembaruan",
                    _isEnglish
                        ? "Could not replace the existing application files. Please try running the installer again."
                        : "Tidak dapat mengganti berkas aplikasi lama. Silakan coba jalankan ulang pembaruan.",
                    isError: true);
            }
        }

        private void OnSecondaryActionClick(object sender, RoutedEventArgs e)
        {
            if (_isWorking) return;

            if (_mode == SetupWindowMode.Install)
            {
                _mode = SetupWindowMode.Uninstall;
                ConfigureModePanelsAndLabels();
            }
            else if (_mode == SetupWindowMode.Update)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/lepi-da-vinci/dynamic-island/releases/latest",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        }

        private void OnLangToggleClick(object sender, RoutedEventArgs e)
        {
            _isEnglish = !_isEnglish;
            try
            {
                LocalizationService.Instance.SetLanguage(_isEnglish ? "en" : "id");
            }
            catch { }

            ApplyLocalization();
        }

        private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            if (_isWorking) return;
            CloseOrShutdown();
        }

        private void CloseOrShutdown()
        {
            if (Owner != null || (Application.Current.MainWindow != null && Application.Current.MainWindow != this))
            {
                Close();
            }
            else
            {
                Close();
                Application.Current.Shutdown();
            }
        }
    }
}
