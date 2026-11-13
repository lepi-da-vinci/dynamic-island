using System.ComponentModel;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Centralized Apple-standard localization engine supporting Bahasa Indonesia ("id") and English ("en").
/// Provides dynamic UI re-translation across MainWindow and SettingsWindow via INotifyPropertyChanged indexer binding.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationService> _lazyInstance = new(() => new LocalizationService());
    public static LocalizationService Instance => _lazyInstance.Value;

    private string _currentLanguage = "id";
    public string CurrentLanguage => _currentLanguage;
    public bool IsEnglish => _currentLanguage.Equals("en", StringComparison.OrdinalIgnoreCase);

    public event Action? LanguageChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly Dictionary<string, string> _idStrings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _enStrings = new(StringComparer.OrdinalIgnoreCase);

    private LocalizationService()
    {
        InitializeStrings();
    }

    public void SetLanguage(string? langCode)
    {
        string normalized = string.Equals(langCode, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "id";
        if (_currentLanguage == normalized) return;

        _currentLanguage = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke();
    }

    public string Format(string key, params object[] args)
    {
        string template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }

    public string Get(string key, string? fallback = null)
    {
        var dict = IsEnglish ? _enStrings : _idStrings;
        if (dict.TryGetValue(key, out string? val) && !string.IsNullOrWhiteSpace(val))
        {
            return val;
        }

        if (IsEnglish && _idStrings.TryGetValue(key, out string? idVal) && !string.IsNullOrWhiteSpace(idVal))
        {
            return idVal;
        }

        return fallback ?? key;
    }

    public string this[string key] => Get(key);

    private void InitializeStrings()
    {
        // ==================== INDONESIAN (id) ====================
        _idStrings["Tab_Media"] = "Musik & Lirik";
        _idStrings["Tab_Todo"] = "Daftar Tugas";
        _idStrings["Tab_Pomodoro"] = "Timer Fokus";
        _idStrings["Tab_Shortcuts"] = "Pintasan";
        _idStrings["Tab_Performance"] = "Performa";
        _idStrings["Tab_Shelf"] = "File Shelf";
        _idStrings["Tab_Calendar"] = "Kalender";
        _idStrings["Tab_Weather"] = "Cuaca";
        _idStrings["Tab_Notifications"] = "Notifikasi";
        _idStrings["Tab_System"] = "Sistem";

        _idStrings["Status_Tuck"] = "Simpan";
        _idStrings["Status_Exit"] = "Keluar";
        _idStrings["Status_Charging"] = "Mengisi Daya";
        _idStrings["Status_Battery"] = "Mode Baterai";
        _idStrings["Status_DesktopAc"] = "Daya AC (100%)";
        _idStrings["Status_Connected"] = "Terhubung";
        _idStrings["Status_Disconnected"] = "Tidak Ada Jaringan";
        _idStrings["Status_MicSafe"] = "Kamera & Mic Aman (Idle)";

        _idStrings["Media_NoActive"] = "Tidak Ada Media Aktif";
        _idStrings["Media_PlayPrompt"] = "Putar lagu di Spotify, Chrome, Edge, atau VLC";
        _idStrings["Media_MixerBtn"] = "Mixer";
        _idStrings["Media_LyricsBtn"] = "Lirik";
        _idStrings["Media_AppMixerTitle"] = "Mixer Volume Aplikasi";
        _idStrings["Media_PrevTooltip"] = "Lagu Sebelumnya";
        _idStrings["Media_PlayTooltip"] = "Putar / Jeda";
        _idStrings["Media_NextTooltip"] = "Lagu Selanjutnya";
        _idStrings["Media_LoadingLyrics"] = "Memuat lirik lagu...";

        _idStrings["Todo_Title"] = "Daftar Tugas";
        _idStrings["Todo_DoneSuffix"] = "Selesai";
        _idStrings["Todo_ClearDone"] = "Hapus Selesai";
        _idStrings["Todo_Placeholder"] = "Ketik tugas baru lalu tekan Enter atau klik Tambah...";
        _idStrings["Todo_AddBtn"] = "Tambah";
        _idStrings["Todo_EmptyTitle"] = "Belum ada daftar tugas hari ini";
        _idStrings["Todo_EmptySub"] = "Klik kolom input di atas, ketik tugas baru, lalu tekan Enter";
        _idStrings["Water_Title"] = "Target Minum Harian";
        _idStrings["Water_LogCup"] = "+ Segelas";
        _idStrings["Water_Reset"] = "Reset";

        _idStrings["Pomo_Title"] = "Timer Fokus";
        _idStrings["Pomo_Start"] = "Mulai Fokus";
        _idStrings["Pomo_Pause"] = "Jeda";
        _idStrings["Pomo_Reset"] = "Reset";
        _idStrings["Pomo_FocusMode"] = "Sesi Fokus";
        _idStrings["Pomo_BreakMode"] = "Istirahat Singkat";

        _idStrings["Sys_Volume"] = "VOLUME AUDIO";
        _idStrings["Sys_Brightness"] = "KECERAHAN LAYAR";
        _idStrings["Sys_Settings"] = "Pengaturan";
        _idStrings["Shortcuts_Title"] = "Pintasan Aplikasi";
        _idStrings["Shortcuts_Add"] = "Tambah App";
        _idStrings["Perf_Title"] = "Performa Sistem";
        _idStrings["Shelf_Files"] = "Rak File";
        _idStrings["Shelf_Clipboard"] = "Riwayat Klip";
        _idStrings["Cal_AgendaTitle"] = "Jadwal & Agenda Hari Ini";
        _idStrings["Notif_EmptyTitle"] = "Belum Ada Notifikasi Sesi Ini";
        _idStrings["Notif_EmptySub"] = "Notifikasi yang masuk akan otomatis dicatat di sini";
        _idStrings["Sys_ControlCenter"] = "System Control Center";
        _idStrings["Sys_PowerControls"] = "Kontrol Daya Sistem";
        _idStrings["Sys_PowerSub"] = "Aksi daya cepat untuk menghemat baterai atau menidurkan sistem seketika";
        _idStrings["Sys_PowerReady"] = "Siap Digunakan";
        _idStrings["Sys_Sleep"] = "Mode Tidur (Sleep)";
        _idStrings["Sys_SleepSub"] = "Stand-by hemat daya";
        _idStrings["Sys_Hibernate"] = "Hibernasi (Hibernate)";
        _idStrings["Sys_HibernateSub"] = "Simpan sesi & matikan";

        _idStrings["Perf_Cpu"] = "BEBAN PROSESOR";
        _idStrings["Perf_Gpu"] = "BEBAN GRAFIS";
        _idStrings["Perf_Ram"] = "PENGGUNAAN MEMORI";
        _idStrings["Perf_Storage"] = "PENYIMPANAN SISTEM";
        _idStrings["Perf_NetDown"] = "UNDUH";
        _idStrings["Perf_NetUp"] = "UNGGAH";
        _idStrings["Perf_Uptime"] = "WAKTU AKTIF";

        _idStrings["Weather_Title"] = "Cuaca & Prakiraan";
        _idStrings["Weather_FeelsLike"] = "Terasa";
        _idStrings["Weather_Humidity"] = "Kelembapan";
        _idStrings["Weather_Wind"] = "Angin";
        _idStrings["Weather_RefreshTooltip"] = "Perbarui Data Cuaca";

        _idStrings["Cal_Title"] = "Kalender & Jadwal";
        _idStrings["Cal_Agenda"] = "Jadwal & Agenda Hari Ini";
        _idStrings["Cal_Add"] = "Tambah";
        _idStrings["Cal_Save"] = "Simpan";

        _idStrings["Notif_Title"] = "Riwayat Notifikasi";
        _idStrings["Notif_SessionSuffix"] = "Sesi Ini";
        _idStrings["Notif_Clear"] = "Bersihkan";
        _idStrings["Notif_Empty"] = "Belum Ada Notifikasi Sesi Ini";

        _idStrings["Shelf_Title"] = "File Shelf";
        _idStrings["Shelf_DropHint"] = "Tarik dan lepaskan file ke sini";
        _idStrings["Shelf_Clear"] = "Kosongkan";

        // Navigation Categories
        _idStrings["Nav_General"] = "Umum";
        _idStrings["Nav_Display"] = "Layar & Posisi";
        _idStrings["Nav_Appearance"] = "Tampilan & Notch";
        _idStrings["Nav_Music"] = "Musik & Lirik";
        _idStrings["Nav_Todo"] = "Daftar Tugas";
        _idStrings["Nav_Calendar"] = "Kalender & Jam";
        _idStrings["Nav_Pomodoro"] = "Timer Fokus";
        _idStrings["Nav_Water"] = "Target Minum";
        _idStrings["Nav_Shelf"] = "File Shelf";
        _idStrings["Nav_Hud"] = "HUD & Kontrol";

        // Settings Items
        _idStrings["Settings_Title"] = "Pengaturan — Windows Dynamic Island";
        _idStrings["Settings_Language"] = "Bahasa Antarmuka";
        _idStrings["Settings_LanguageSub"] = "Pilih bahasa tampilan untuk Dynamic Island dan Pengaturan";
        _idStrings["Settings_StartWithWindows"] = "Start with Windows";
        _idStrings["Settings_StartWithWindowsSub"] = "Jalankan Dynamic Island otomatis saat Windows menyala";
        _idStrings["Settings_Size"] = "Ukuran Notch";
        _idStrings["Settings_SizeSub"] = "Skala ukuran Dynamic Island diterapkan langsung secara real-time";
        _idStrings["Settings_MinimalNotch"] = "Minimal Notch Mode";
        _idStrings["Settings_MinimalNotchSub"] = "Tampilan notch ultra-ramping untuk ruang kerja maksimal";
        _idStrings["Settings_AutoTuck"] = "Simpan Otomatis (Auto-Tuck)";
        _idStrings["Settings_AutoTuckSub"] = "Kecilkan notch menjadi garis tipis di atas layar saat idle";
        _idStrings["Settings_IdleDuration"] = "Durasi Sebelum Simpan";
        _idStrings["Settings_IdleDurationSub"] = "Waktu tunggu dalam detik sebelum notch masuk ke mode simpan";
        _idStrings["Settings_DashboardTabs"] = "Pilihan Tab Notch";
        _idStrings["Settings_DashboardTabsSub"] = "Aktifkan atau nonaktifkan tab dashboard yang ingin ditampilkan";
        _idStrings["Settings_AutoHideFullscreen"] = "Sembunyikan Saat Layar Penuh";
        _idStrings["Settings_AutoHideFullscreenSub"] = "Sembunyikan otomatis saat membuka game atau video layar penuh";
        _idStrings["Settings_ScreenshotTuck"] = "Simpan Otomatis Saat Screenshot";
        _idStrings["Settings_ScreenshotTuckSub"] = "Sembunyikan notch ke mode simpan saat mengambil tangkapan layar";
        _idStrings["Settings_ExcludeCapture"] = "Kecualikan dari Rekam Layar";
        _idStrings["Settings_ExcludeCaptureSub"] = "Gunakan Windows Display Affinity agar notch tidak terekam di OBS/rekaman";
        _idStrings["Settings_TopOffset"] = "Jarak dari Atas Layar";
        _idStrings["Settings_PlacementStyle"] = "Gaya Penempatan Island";
        _idStrings["Settings_PlacementStyleSub"] = "Notch (Menempel Atas) atau Punch Hole (Kapsul Melayang)";
        _idStrings["Settings_Theme"] = "Tema Permukaan";
        _idStrings["Settings_ThemeSub"] = "Pilihan material visual Apple OLED dan Glassmorphism";
        _idStrings["Settings_Accent"] = "Aksen Warna";
        _idStrings["Settings_AccentSub"] = "Warna aksen sistem atau sesuaikan otomatis dengan cover album musik";
        _idStrings["Settings_CornerStyle"] = "Gaya Sudut Notch";
        _idStrings["Settings_GlowStyle"] = "Gaya Kilau Tepi (Rim Glow)";
        _idStrings["Settings_Lyrics"] = "Lirik Lagu Real-time";
        _idStrings["Settings_LyricsSub"] = "Tampilkan lirik bersinkronisasi dari LRCLIB atau NetEase";
        _idStrings["Settings_SlowPan"] = "Animasi Geser Lirik Halus (Slow Pan)";
        _idStrings["Settings_SlowPanSub"] = "Geser lirik panjang secara perlahan sesuai ritme lagu";
        _idStrings["Settings_Visualizer"] = "Visualizer Audio Gelombang";
        _idStrings["Settings_VisualizerSub"] = "Equalizer 5-band spektrum musik real-time sinkron ke audio";
        _idStrings["Settings_HudVolume"] = "HUD Volume & Kecerahan";
        _idStrings["Settings_HudVolumeSub"] = "Tampilkan pill modern saat tombol volume atau kecerahan ditekan";
        _idStrings["Settings_SuppressOsd"] = "Hapus OSD Bawaan Windows";
        _idStrings["Settings_SuppressOsdSub"] = "Sembunyikan bar volume/kecerahan bawaan Windows yang mengganggu";
        _idStrings["Settings_BatteryToast"] = "Notifikasi Pengisian Baterai";
        _idStrings["Settings_BatteryToastSub"] = "Tampilkan pill status saat charger dicolok atau dicabut";
        _idStrings["Settings_Battery80"] = "Peringatan Baterai 80% (Smart Charge)";
        _idStrings["Settings_Battery80Sub"] = "Ingatkan saat baterai mencapai 80% untuk memperpanjang usia baterai";
        _idStrings["Settings_BluetoothToast"] = "Notifikasi Perangkat Bluetooth";
        _idStrings["Settings_BluetoothToastSub"] = "Tampilkan popup saat headphone/TWS atau perangkat periferal terhubung";
        _idStrings["Settings_SystemNotif"] = "Notifikasi Sistem Windows";
        _idStrings["Settings_SystemNotifSub"] = "Tampilkan notifikasi aplikasi ke dalam Dynamic Island pill";
        _idStrings["Settings_ToastDuration"] = "Durasi Tampilan Notifikasi";

        // ==================== ENGLISH (en) ====================
        _enStrings["Tab_Media"] = "Music & Lyrics";
        _enStrings["Tab_Todo"] = "To-Do List";
        _enStrings["Tab_Pomodoro"] = "Focus Timer";
        _enStrings["Tab_Shortcuts"] = "Shortcuts";
        _enStrings["Tab_Performance"] = "Performance";
        _enStrings["Tab_Shelf"] = "File Shelf";
        _enStrings["Tab_Calendar"] = "Calendar";
        _enStrings["Tab_Weather"] = "Weather";
        _enStrings["Tab_Notifications"] = "Notifications";
        _enStrings["Tab_System"] = "System";

        _enStrings["Status_Tuck"] = "Tuck";
        _enStrings["Status_Exit"] = "Exit";
        _enStrings["Status_Charging"] = "Charging";
        _enStrings["Status_Battery"] = "On Battery";
        _enStrings["Status_DesktopAc"] = "AC Power (100%)";
        _enStrings["Status_Connected"] = "Connected";
        _enStrings["Status_Disconnected"] = "No Connection";
        _enStrings["Status_MicSafe"] = "Camera & Mic Safe (Idle)";

        _enStrings["Media_NoActive"] = "No Active Media";
        _enStrings["Media_PlayPrompt"] = "Play media in Spotify, Chrome, Edge, or VLC";
        _enStrings["Media_MixerBtn"] = "Mixer";
        _enStrings["Media_LyricsBtn"] = "Lyrics";
        _enStrings["Media_AppMixerTitle"] = "App Volume Mixer";
        _enStrings["Media_PrevTooltip"] = "Previous Track";
        _enStrings["Media_PlayTooltip"] = "Play / Pause";
        _enStrings["Media_NextTooltip"] = "Next Track";
        _enStrings["Media_LoadingLyrics"] = "Loading lyrics...";

        _enStrings["Todo_Title"] = "To-Do List";
        _enStrings["Todo_DoneSuffix"] = "Done";
        _enStrings["Todo_ClearDone"] = "Clear Done";
        _enStrings["Todo_Placeholder"] = "Type a new task then press Enter or click Add...";
        _enStrings["Todo_AddBtn"] = "Add";
        _enStrings["Todo_EmptyTitle"] = "No tasks for today";
        _enStrings["Todo_EmptySub"] = "Click the input box above, type a new task, and press Enter";
        _enStrings["Water_Title"] = "Daily Water Intake";
        _enStrings["Water_LogCup"] = "+ 1 Cup";
        _enStrings["Water_Reset"] = "Reset";

        _enStrings["Pomo_Title"] = "Focus Timer";
        _enStrings["Pomo_Start"] = "Start Focus";
        _enStrings["Pomo_Pause"] = "Pause";
        _enStrings["Pomo_Reset"] = "Reset";
        _enStrings["Pomo_FocusMode"] = "Focus Session";
        _enStrings["Pomo_BreakMode"] = "Short Break";

        _enStrings["Sys_Volume"] = "AUDIO VOLUME";
        _enStrings["Sys_Brightness"] = "SCREEN BRIGHTNESS";
        _enStrings["Sys_Settings"] = "Settings";
        _enStrings["Shortcuts_Title"] = "App Shortcuts";
        _enStrings["Shortcuts_Add"] = "Add App";
        _enStrings["Perf_Title"] = "System Performance";
        _enStrings["Shelf_Files"] = "Files";
        _enStrings["Shelf_Clipboard"] = "Clipboard";
        _enStrings["Cal_AgendaTitle"] = "Today's Schedule & Agenda";
        _enStrings["Notif_EmptyTitle"] = "No Notifications This Session";
        _enStrings["Notif_EmptySub"] = "Incoming notifications will automatically appear here";
        _enStrings["Sys_ControlCenter"] = "System Control Center";
        _enStrings["Sys_PowerControls"] = "System Power Controls";
        _enStrings["Sys_PowerSub"] = "Quick power actions to save power or sleep system immediately";
        _enStrings["Sys_PowerReady"] = "Ready";
        _enStrings["Sys_Sleep"] = "Sleep Mode";
        _enStrings["Sys_SleepSub"] = "Low-power standby";
        _enStrings["Sys_Hibernate"] = "Hibernate";
        _enStrings["Sys_HibernateSub"] = "Save session & shut down";

        _enStrings["Perf_Cpu"] = "PROCESSOR LOAD";
        _enStrings["Perf_Gpu"] = "GRAPHICS LOAD";
        _enStrings["Perf_Ram"] = "MEMORY USAGE";
        _enStrings["Perf_Storage"] = "SYSTEM STORAGE";
        _enStrings["Perf_NetDown"] = "DOWNLOAD";
        _enStrings["Perf_NetUp"] = "UPLOAD";
        _enStrings["Perf_Uptime"] = "UPTIME";

        _enStrings["Weather_Title"] = "Weather & Forecast";
        _enStrings["Weather_FeelsLike"] = "Feels like";
        _enStrings["Weather_Humidity"] = "Humidity";
        _enStrings["Weather_Wind"] = "Wind";
        _enStrings["Weather_RefreshTooltip"] = "Refresh Weather Data";

        _enStrings["Cal_Title"] = "Calendar & Schedule";
        _enStrings["Cal_Agenda"] = "Today's Agenda & Schedule";
        _enStrings["Cal_Add"] = "Add";
        _enStrings["Cal_Save"] = "Save";

        _enStrings["Notif_Title"] = "Notification History";
        _enStrings["Notif_SessionSuffix"] = "This Session";
        _enStrings["Notif_Clear"] = "Clear All";
        _enStrings["Notif_Empty"] = "No Notifications This Session";

        _enStrings["Shelf_Title"] = "File Shelf";
        _enStrings["Shelf_DropHint"] = "Drag and drop files here";
        _enStrings["Shelf_Clear"] = "Clear";

        // Navigation Categories
        _enStrings["Nav_General"] = "General";
        _enStrings["Nav_Display"] = "Display & Position";
        _enStrings["Nav_Appearance"] = "Appearance & Notch";
        _enStrings["Nav_Music"] = "Music & Lyrics";
        _enStrings["Nav_Todo"] = "To-Do List";
        _enStrings["Nav_Calendar"] = "Calendar & Clock";
        _enStrings["Nav_Pomodoro"] = "Focus Timer";
        _enStrings["Nav_Water"] = "Water Intake";
        _enStrings["Nav_Shelf"] = "File Shelf";
        _enStrings["Nav_Hud"] = "HUD & Controls";

        // Settings Items
        _enStrings["Settings_Title"] = "Settings — Windows Dynamic Island";
        _idStrings["Settings_Language"] = "Bahasa Antarmuka";
        _enStrings["Settings_Language"] = "Interface Language";
        _enStrings["Settings_LanguageSub"] = "Choose display language for Dynamic Island and Settings";
        _enStrings["Settings_StartWithWindows"] = "Start with Windows";
        _enStrings["Settings_StartWithWindowsSub"] = "Launch Dynamic Island automatically when Windows starts";
        _enStrings["Settings_Size"] = "Notch Size";
        _enStrings["Settings_SizeSub"] = "Dynamic Island scale applied in real-time";
        _enStrings["Settings_MinimalNotch"] = "Minimal Notch Mode";
        _enStrings["Settings_MinimalNotchSub"] = "Ultra-slim notch layout for maximum screen real estate";
        _enStrings["Settings_AutoTuck"] = "Auto-Tuck Mode";
        _enStrings["Settings_AutoTuckSub"] = "Tuck notch into a thin line at the top when idle";
        _enStrings["Settings_IdleDuration"] = "Idle Delay Before Tuck";
        _enStrings["Settings_IdleDurationSub"] = "Wait time in seconds before notch enters tucked mode";
        _enStrings["Settings_DashboardTabs"] = "Dashboard Tab Selection";
        _enStrings["Settings_DashboardTabsSub"] = "Enable or disable dashboard tabs to display";
        _enStrings["Settings_AutoHideFullscreen"] = "Hide on Fullscreen";
        _enStrings["Settings_AutoHideFullscreenSub"] = "Automatically hide when running full-screen games or videos";
        _enStrings["Settings_ScreenshotTuck"] = "Auto-Tuck on Screenshot";
        _enStrings["Settings_ScreenshotTuckSub"] = "Tuck notch to top edge while taking screenshots";
        _enStrings["Settings_ExcludeCapture"] = "Exclude from Screen Capture";
        _enStrings["Settings_ExcludeCaptureSub"] = "Use Windows Display Affinity to keep notch hidden in recordings/OBS";
        _enStrings["Settings_TopOffset"] = "Top Screen Offset";
        _enStrings["Settings_PlacementStyle"] = "Island Placement Style";
        _enStrings["Settings_PlacementStyleSub"] = "Notch (Top-Attached) or Punch Hole (Floating Capsule)";
        _enStrings["Settings_Theme"] = "Surface Theme";
        _enStrings["Settings_ThemeSub"] = "Apple OLED and Glassmorphism visual materials";
        _enStrings["Settings_Accent"] = "Accent Color";
        _enStrings["Settings_AccentSub"] = "System accent color or auto-match with album art";
        _enStrings["Settings_CornerStyle"] = "Notch Corner Style";
        _enStrings["Settings_GlowStyle"] = "Rim Glow Style";
        _enStrings["Settings_Lyrics"] = "Real-time Song Lyrics";
        _enStrings["Settings_LyricsSub"] = "Display synchronized lyrics from LRCLIB or NetEase";
        _enStrings["Settings_SlowPan"] = "Smooth Lyric Panning (Slow Pan)";
        _enStrings["Settings_SlowPanSub"] = "Slowly pan long lyric lines in sync with track rhythm";
        _enStrings["Settings_Visualizer"] = "Waveform Audio Visualizer";
        _enStrings["Settings_VisualizerSub"] = "Real-time 5-band music spectrum synchronized with output";
        _enStrings["Settings_HudVolume"] = "Volume & Brightness HUD";
        _enStrings["Settings_HudVolumeSub"] = "Show modern pill when volume or brightness keys are pressed";
        _enStrings["Settings_SuppressOsd"] = "Suppress Windows Native OSD";
        _enStrings["Settings_SuppressOsdSub"] = "Hide the intrusive default Windows volume/brightness flyout";
        _enStrings["Settings_BatteryToast"] = "Battery Charging Toast";
        _enStrings["Settings_BatteryToastSub"] = "Show status pill when charger is connected or unplugged";
        _enStrings["Settings_Battery80"] = "80% Battery Alert (Smart Charge)";
        _enStrings["Settings_Battery80Sub"] = "Remind when battery reaches 80% to protect lithium battery health";
        _enStrings["Settings_BluetoothToast"] = "Bluetooth Device Toast";
        _enStrings["Settings_BluetoothToastSub"] = "Show popup when headphones/TWS or peripherals connect";
        _enStrings["Settings_SystemNotif"] = "Windows System Notifications";
        _enStrings["Settings_SystemNotifSub"] = "Forward Windows app notifications into Dynamic Island pill";
        _enStrings["Settings_ToastDuration"] = "Toast Notification Duration";

        // ==================== EXTENDED LOCALIZATION (v2.1.0) ====================
        // Settings Window Sidebar, Headers & Controls
        _idStrings["Settings_CloseTooltip"] = "Tutup Pengaturan";
        _enStrings["Settings_CloseTooltip"] = "Close Settings";
        _idStrings["Settings_DoneBtn"] = "Selesai";
        _enStrings["Settings_DoneBtn"] = "Done";
        _idStrings["Settings_HeaderLangSection"] = "Bahasa & Lokalisasi";
        _enStrings["Settings_HeaderLangSection"] = "Language & Localization";
        _idStrings["Settings_HeaderStartup"] = "Startup";
        _enStrings["Settings_HeaderStartup"] = "Startup";
        _idStrings["Settings_HeaderNotch"] = "Notch & Ukuran";
        _enStrings["Settings_HeaderNotch"] = "Notch & Sizing";
        _idStrings["Settings_HeaderNotchTabs"] = "Tab Notch (Kustomisasi Tab)";
        _enStrings["Settings_HeaderNotchTabs"] = "Notch Tabs (Customize)";
        _idStrings["Settings_HeaderUpdater"] = "Pembaruan Aplikasi";
        _enStrings["Settings_HeaderUpdater"] = "Application Updates";
        _idStrings["Settings_UpdaterSub"] = "Memeriksa pembaruan otomatis dari GitHub Releases";
        _enStrings["Settings_UpdaterSub"] = "Automatically check and install releases from GitHub";
        _idStrings["Settings_CheckUpdateBtn"] = "Cek Update";
        _enStrings["Settings_CheckUpdateBtn"] = "Check Update";
        _idStrings["Settings_UpdateNowBtn"] = "Update Sekarang";
        _enStrings["Settings_UpdateNowBtn"] = "Update Now";
        _idStrings["Settings_PowerTitle"] = "Kontrol Daya Sistem";
        _enStrings["Settings_PowerTitle"] = "System Power Controls";
        _idStrings["Settings_PowerSub"] = "Aksi daya cepat untuk menghemat baterai atau menidurkan sistem seketika";
        _enStrings["Settings_PowerSub"] = "Quick power actions to conserve battery or sleep your PC immediately";
        _idStrings["Settings_Sec5"] = "5 Detik";
        _enStrings["Settings_Sec5"] = "5s";
        _idStrings["Settings_Sec10"] = "10 Detik";
        _enStrings["Settings_Sec10"] = "10s";
        _idStrings["Settings_Sec15"] = "15 Detik";
        _enStrings["Settings_Sec15"] = "15s";
        _idStrings["Settings_Sec30"] = "30 Detik";
        _enStrings["Settings_Sec30"] = "30s";
        _idStrings["Settings_TabMusic"] = "Tab Musik";
        _enStrings["Settings_TabMusic"] = "Music Tab";
        _idStrings["Settings_TabMusicSub"] = "Tampilkan tab pemutar media & panggung lirik lagu di Dynamic Island";
        _enStrings["Settings_TabMusicSub"] = "Show media player and synchronized lyrics stage in Dynamic Island";
        _idStrings["Settings_TabTodo"] = "Tab Tugas (To-do)";
        _enStrings["Settings_TabTodo"] = "To-Do Tab";
        _idStrings["Settings_TabTodoSub"] = "Tampilkan tab daftar tugas harian di Dynamic Island";
        _enStrings["Settings_TabTodoSub"] = "Show daily task list tab in Dynamic Island";
        _idStrings["Settings_TabFocus"] = "Tab Fokus (Pomodoro & Air)";
        _enStrings["Settings_TabFocus"] = "Focus Tab (Timer & Hydration)";
        _idStrings["Settings_TabFocusSub"] = "Tampilkan tab timer fokus Pomodoro, Stopwatch & target hidrasi";
        _enStrings["Settings_TabFocusSub"] = "Show Pomodoro focus timer, stopwatch, and hydration tracker tab";
        _idStrings["Settings_TabShortcuts"] = "Tab Pintasan Aplikasi";
        _enStrings["Settings_TabShortcuts"] = "App Shortcuts Tab";
        _idStrings["Settings_TabShortcutsSub"] = "Tampilkan Launchpad pintasan cepat untuk membuka aplikasi tanpa Start Menu";
        _enStrings["Settings_TabShortcutsSub"] = "Show quick Launchpad shortcuts to open apps without the Start Menu";
        _idStrings["Settings_TabPerf"] = "Tab Monitor Performa (CPU / GPU / RAM)";
        _enStrings["Settings_TabPerf"] = "Performance Monitor Tab (CPU / GPU / RAM)";
        _idStrings["Settings_TabPerfSub"] = "Tampilkan pemantau performa perangkat real-time (CPU, GPU, RAM, Disk, Jaringan)";
        _enStrings["Settings_TabPerfSub"] = "Show real-time hardware telemetry (CPU, GPU, RAM, Disk, Network)";
        _idStrings["Settings_TabShelf"] = "Tab Rak File (Shelf)";
        _enStrings["Settings_TabShelf"] = "File Shelf Tab";
        _enStrings["Settings_TabShelfSub"] = "Tampilkan rak file drag-and-drop & berbagi QR Wi-Fi lokal";
        _enStrings["Settings_TabShelfSub"] = "Show temporary drag-and-drop shelf and local Wi-Fi QR sharing";

        // Display & Multi-Monitor
        _idStrings["Settings_HeaderScreen"] = "Layar & Perilaku";
        _enStrings["Settings_HeaderScreen"] = "Screen & Behavior";
        _idStrings["Settings_TargetMonitor"] = "Monitor Target Dynamic Island";
        _enStrings["Settings_TargetMonitor"] = "Target Display Monitor";
        _idStrings["Settings_TargetMonitorSub"] = "Pilih layar tempat Dynamic Island ditampilkan atau ikuti kursor aktif";
        _enStrings["Settings_TargetMonitorSub"] = "Choose which display hosts Dynamic Island or follow the active cursor";
        _idStrings["Monitor_Primary"] = "Utama";
        _enStrings["Monitor_Primary"] = "Primary";
        _idStrings["Monitor_Display2"] = "Layar 2";
        _enStrings["Monitor_Display2"] = "Display 2";
        _idStrings["Monitor_Display3"] = "Layar 3";
        _enStrings["Monitor_Display3"] = "Display 3";
        _idStrings["Monitor_FollowCursor"] = "Ikuti Kursor";
        _enStrings["Monitor_FollowCursor"] = "Follow Cursor";
        _idStrings["Settings_TopOffsetSub"] = "Jarak vertikal Dynamic Island dari tepi paling atas layar monitor";
        _enStrings["Settings_TopOffsetSub"] = "Vertical gap between Dynamic Island and the top edge of the monitor";

        // Appearance Cards
        _idStrings["Settings_HeaderContour"] = "Model Kontur, Tema Permukaan & Material";
        _enStrings["Settings_HeaderContour"] = "Contour Shape, Surface Theme & Material";
        _idStrings["Settings_NotchMode"] = "Notch Menempel Atas";
        _enStrings["Settings_NotchMode"] = "Top-Attached Notch";
        _idStrings["Settings_PunchHoleMode"] = "Punch-Hole Melayang";
        _enStrings["Settings_PunchHoleMode"] = "Floating Punch-Hole";
        _idStrings["Settings_CornerStyleSub"] = "Sesuaikan siluet lengkungan telinga atas dan sudut bawah Dynamic Island";
        _enStrings["Settings_CornerStyleSub"] = "Customize the top-ear curve and bottom corner silhouette";
        _idStrings["Settings_CornerSquircle"] = "Apple Squircle";
        _enStrings["Settings_CornerSquircle"] = "Apple Squircle";
        _idStrings["Settings_CornerUltraPill"] = "Ultra Pill Bulat";
        _enStrings["Settings_CornerUltraPill"] = "Ultra Pill";
        _idStrings["Settings_CornerSharp"] = "Sharp Modern";
        _enStrings["Settings_CornerSharp"] = "Sharp Modern";
        _idStrings["Settings_Opacity"] = "Transparansi Permukaan";
        _enStrings["Settings_Opacity"] = "Surface Opacity";
        _idStrings["Settings_OpacitySub"] = "Tingkat kepekatan latar belakang OLED / Glassmorphism pada pulau";
        _enStrings["Settings_OpacitySub"] = "Background opacity level for OLED and Glassmorphism materials";
        _idStrings["Settings_HeaderGlass"] = "Glassmorphism & Aksen";
        _enStrings["Settings_HeaderGlass"] = "Glassmorphism & Accent";
        _idStrings["Settings_RimSubtle"] = "Kaca Tipis (Default)";
        _enStrings["Settings_RimSubtle"] = "Subtle Glass (Default)";
        _idStrings["Settings_RimNeon"] = "Neon Warna Aksen";
        _enStrings["Settings_RimNeon"] = "Accent Neon";
        _idStrings["Settings_RimHidden"] = "Tanpa Garis Tepi";
        _enStrings["Settings_RimHidden"] = "Hidden";
        _idStrings["Settings_GlowStyleSub"] = "Tampilan garis kilau kaca atau neon di sekeliling kontur pulau";
        _enStrings["Settings_GlowStyleSub"] = "Edge rim highlight appearance around the island contour";
        _idStrings["Settings_Backlight"] = "Cahaya Latar Ambient";
        _enStrings["Settings_Backlight"] = "Ambient Backlight Glow";
        _idStrings["Settings_BacklightSub"] = "Intensitas pendaran cahaya warna di belakang kontur Dynamic Island";
        _enStrings["Settings_BacklightSub"] = "Ambient color halo intensity behind the Dynamic Island contour";
        _idStrings["Settings_GlowOff"] = "Mati (0%)";
        _enStrings["Settings_GlowOff"] = "Off (0%)";
        _idStrings["Settings_GlowLow"] = "Lembut";
        _enStrings["Settings_GlowLow"] = "Soft";
        _idStrings["Settings_GlowNorm"] = "Normal";
        _enStrings["Settings_GlowNorm"] = "Normal";
        _idStrings["Settings_GlowHigh"] = "Terang";
        _enStrings["Settings_GlowHigh"] = "Bright";
        _idStrings["Settings_GlassRim"] = "Garis Kilau Kaca (Rim)";
        _enStrings["Settings_GlassRim"] = "Glass Contour Rim";
        _idStrings["Settings_GlassRimSub"] = "Tampilkan garis kilau kaca tipis pada tepi kurva Dynamic Island";
        _enStrings["Settings_GlassRimSub"] = "Show thin specular glass highlight along the notch outline";

        // Music, Todo, Calendar, Pomodoro, Water, Shelf, HUD Pages
        _idStrings["Settings_HeaderMusic"] = "Pemutar & Lirik Tersinkronisasi";
        _enStrings["Settings_HeaderMusic"] = "Playback & Synchronized Lyrics";
        _idStrings["Settings_TrackPopup"] = "Popup Pergantian Lagu";
        _enStrings["Settings_TrackPopup"] = "Track Change Popup";
        _idStrings["Settings_TrackPopupSub"] = "Tampilkan notifikasi judul & artis saat lagu berganti";
        _enStrings["Settings_TrackPopupSub"] = "Show title & artist pill when switching songs";
        _idStrings["Settings_AppMixer"] = "Mixer Audio Per Aplikasi";
        _enStrings["Settings_AppMixer"] = "Per-App Audio Mixer";
        _idStrings["Settings_AppMixerSub"] = "Aktifkan kontrol volume mandiri untuk Spotify, browser, dan game di tab Musik";
        _enStrings["Settings_AppMixerSub"] = "Enable per-app volume sliders for Spotify, browsers, and games in Music tab";

        _idStrings["Settings_HeaderTodo"] = "Daftar Tugas & Indikator";
        _enStrings["Settings_HeaderTodo"] = "Task List & Indicators";
        _idStrings["Settings_TaskBadge"] = "Tampilkan Badge Tugas di Notch";
        _enStrings["Settings_TaskBadge"] = "Show Active Task Badge on Notch";
        _idStrings["Settings_TaskBadgeSub"] = "Tampilkan jumlah tugas aktif pada pil idle Dynamic Island";
        _enStrings["Settings_TaskBadgeSub"] = "Show active task counter badge on the idle Dynamic Island pill";
        _idStrings["Settings_ClearDoneTasks"] = "Bersihkan Tugas Selesai";
        _enStrings["Settings_ClearDoneTasks"] = "Clear Completed Tasks";
        _idStrings["Settings_ClearDoneTasksSub"] = "Hapus seluruh tugas yang sudah ditandai selesai dari daftar";
        _enStrings["Settings_ClearDoneTasksSub"] = "Remove all completed tasks from your active list";

        _idStrings["Settings_HeaderClock"] = "Tampilan Tanggal & Jam";
        _enStrings["Settings_HeaderClock"] = "Date & Clock Display";
        _idStrings["Settings_ShowDate"] = "Tampilkan Tanggal di Pil Idle";
        _enStrings["Settings_ShowDate"] = "Show Date on Idle Pill";
        _idStrings["Settings_ShowDateSub"] = "Tampilkan tanggal hari ini di sebelah kiri jam";
        _enStrings["Settings_ShowDateSub"] = "Display today's date beside the clock on the idle pill";
        _idStrings["Settings_24Hour"] = "Format Waktu 24-Jam";
        _enStrings["Settings_24Hour"] = "24-Hour Clock Format";
        _idStrings["Settings_24HourSub"] = "Gunakan format 24-jam (16:30) alih-alih 12-jam";
        _enStrings["Settings_24HourSub"] = "Use 24-hour time (16:30) instead of 12-hour AM/PM";
        _idStrings["Settings_ShowSeconds"] = "Tampilkan Detik pada Jam";
        _enStrings["Settings_ShowSeconds"] = "Show Seconds on Clock";
        _idStrings["Settings_ShowSecondsSub"] = "Tampilkan detik berjalan pada indikator waktu utama";
        _enStrings["Settings_ShowSecondsSub"] = "Display ticking seconds on the primary clock readout";

        _idStrings["Settings_HeaderPomo"] = "Timer Fokus & Istirahat";
        _enStrings["Settings_HeaderPomo"] = "Focus & Break Timer";
        _idStrings["Settings_FocusDur"] = "Durasi Fokus Standar";
        _enStrings["Settings_FocusDur"] = "Default Focus Duration";
        _idStrings["Settings_FocusDurSub"] = "Durasi standar sesi fokus saat tombol Fokus ditekan";
        _enStrings["Settings_FocusDurSub"] = "Default duration of a focus session when started";
        _idStrings["Settings_BreakDur"] = "Durasi Istirahat Standar";
        _enStrings["Settings_BreakDur"] = "Default Break Duration";
        _idStrings["Settings_BreakDurSub"] = "Durasi istirahat setelah sesi fokus selesai";
        _enStrings["Settings_BreakDurSub"] = "Break length after completing a focus session";
        _idStrings["Settings_PomoIdle"] = "Hitung Mundur Langsung di Pil Idle";
        _enStrings["Settings_PomoIdle"] = "Live Countdown on Idle Pill";
        _idStrings["Settings_PomoIdleSub"] = "Tampilkan progres timer & stopwatch di sayap sisi saat berjalan";
        _enStrings["Settings_PomoIdleSub"] = "Show live timer & stopwatch progress on the side wing while active";
        _idStrings["Settings_Min15"] = "15 Menit";
        _enStrings["Settings_Min15"] = "15 Min";
        _idStrings["Settings_Min25"] = "25 Menit";
        _enStrings["Settings_Min25"] = "25 Min";
        _idStrings["Settings_Min45"] = "45 Menit";
        _enStrings["Settings_Min45"] = "45 Min";
        _idStrings["Settings_Min60"] = "60 Menit";
        _enStrings["Settings_Min60"] = "60 Min";
        _idStrings["Settings_Min5"] = "5 Menit";
        _enStrings["Settings_Min5"] = "5 Min";
        _idStrings["Settings_Min10"] = "10 Menit";
        _enStrings["Settings_Min10"] = "10 Min";

        _idStrings["Settings_HeaderWater"] = "Hidrasi & Target Harian";
        _enStrings["Settings_HeaderWater"] = "Hydration & Daily Target";
        _idStrings["Settings_WaterTarget"] = "Target Air Minum Harian";
        _enStrings["Settings_WaterTarget"] = "Daily Water Target";
        _idStrings["Settings_WaterTargetSub"] = "Target konsumsi air minum harian Anda";
        _enStrings["Settings_WaterTargetSub"] = "Your daily hydration consumption goal";
        _idStrings["Settings_WaterAddReset"] = "Tambah & Reset Air Harian";
        _enStrings["Settings_WaterAddReset"] = "Log & Reset Daily Water";
        _idStrings["Settings_WaterProgressFmt"] = "Progres hari ini: {0:F1} / {1:F1} L";
        _enStrings["Settings_WaterProgressFmt"] = "Today's progress: {0:F1} / {1:F1} L";

        _idStrings["Settings_HeaderShelf"] = "Rak File & Drag-Drop";
        _enStrings["Settings_HeaderShelf"] = "File Drop Tray";
        _idStrings["Settings_AutoShelf"] = "Buka Rak Otomatis Saat Drag File";
        _enStrings["Settings_AutoShelf"] = "Auto-open Shelf on File Drag";
        _idStrings["Settings_AutoShelfSub"] = "Buka Rak File otomatis saat menarik file ke area Dynamic Island";
        _enStrings["Settings_AutoShelfSub"] = "Automatically open File Shelf when dragging files over Dynamic Island";
        _idStrings["Settings_ClearShelf"] = "Bersihkan Rak File";
        _enStrings["Settings_ClearShelf"] = "Clear File Shelf";
        _idStrings["Settings_ClearShelfSub"] = "Kosongkan seluruh file sementara dari Rak File Dynamic Island";
        _enStrings["Settings_ClearShelfSub"] = "Remove all temporary items from the Dynamic Island File Shelf";
        _idStrings["Settings_ClearShelfBtn"] = "Kosongkan Rak";
        _enStrings["Settings_ClearShelfBtn"] = "Empty Shelf";

        _idStrings["Settings_HeaderBt"] = "Kontrol & Pairing Bluetooth";
        _enStrings["Settings_HeaderBt"] = "Bluetooth Control & Pairing";
        _idStrings["Settings_BtSub"] = "Siap menghubungkan perangkat audio & aksesoris";
        _enStrings["Settings_BtSub"] = "Ready to connect wireless audio and accessories";
        _idStrings["Settings_BtToggleBtn"] = "Matikan / Nyalakan";
        _enStrings["Settings_BtToggleBtn"] = "Toggle Radio";
        _idStrings["Settings_BtPairBtn"] = "Pair Perangkat";
        _enStrings["Settings_BtPairBtn"] = "Pair Device";
        _idStrings["Settings_HeaderHud"] = "HUD & Notifikasi Toast";
        _enStrings["Settings_HeaderHud"] = "HUD & Toast Notifications";
        _idStrings["Settings_SpotlightTitle"] = "Spotlight Quick Launcher (Alt + Spasi)";
        _enStrings["Settings_SpotlightTitle"] = "Spotlight Quick Launcher (Alt + Space)";
        _idStrings["Settings_SpotlightSub"] = "Buka launcher cepat untuk aplikasi, OCR layar, color picker, kalkulator & perintah sistem";
        _enStrings["Settings_SpotlightSub"] = "Open quick launcher for apps, screen OCR, color picker, calculator, and system commands";
        _idStrings["Settings_ToastDurSub"] = "Lama notifikasi tampil sebelum kembali ke tampilan normal";
        _enStrings["Settings_ToastDurSub"] = "Duration a toast remains visible before returning to normal state";
        _idStrings["Settings_Dur20"] = "2.0 Detik";
        _enStrings["Settings_Dur20"] = "2.0s";
        _idStrings["Settings_Dur28"] = "2.8 Detik";
        _enStrings["Settings_Dur28"] = "2.8s";
        _idStrings["Settings_Dur40"] = "4.0 Detik";
        _enStrings["Settings_Dur40"] = "4.0s";
        _idStrings["Settings_Dur50"] = "5.0 Detik";
        _enStrings["Settings_Dur50"] = "5.0s";

        // Service Toasts & Runtime Localization
        _idStrings["Toast_Battery80Title"] = "Baterai Mencapai 80%";
        _enStrings["Toast_Battery80Title"] = "Battery Reached 80%";
        _idStrings["Toast_Battery80Sub"] = "Smart Charge: Cabut charger untuk jaga kesehatan baterai";
        _enStrings["Toast_Battery80Sub"] = "Smart Charge: Unplug charger to preserve battery lifespan";
        _idStrings["Toast_ChargerPluggedTitle"] = "Daya Tersambung ({0}%)";
        _enStrings["Toast_ChargerPluggedTitle"] = "Power Connected ({0}%)";
        _idStrings["Toast_ChargerPluggedSub"] = "Laptop sedang mengisi daya";
        _enStrings["Toast_ChargerPluggedSub"] = "Laptop is now charging";
        _idStrings["Toast_ChargerUnpluggedTitle"] = "Mode Baterai ({0}%)";
        _enStrings["Toast_ChargerUnpluggedTitle"] = "On Battery ({0}%)";
        _idStrings["Toast_ChargerUnpluggedSub"] = "Charger dilepas • Berjalan di daya baterai";
        _enStrings["Toast_ChargerUnpluggedSub"] = "Charger unplugged • Running on battery power";
        _idStrings["Toast_BtConnectedTitle"] = "Bluetooth Terhubung";
        _enStrings["Toast_BtConnectedTitle"] = "Bluetooth Connected";
        _idStrings["Toast_BtConnectedSub"] = "{0} siap digunakan";
        _enStrings["Toast_BtConnectedSub"] = "{0} ready for audio and control";
        _idStrings["Toast_BtDisconnectedTitle"] = "Bluetooth Terputus";
        _enStrings["Toast_BtDisconnectedTitle"] = "Bluetooth Disconnected";
        _idStrings["Toast_BtDisconnectedSub"] = "{0} tidak lagi terhubung";
        _enStrings["Toast_BtDisconnectedSub"] = "{0} is no longer connected";
        _idStrings["Toast_BtOnTitle"] = "Bluetooth Aktif";
        _enStrings["Toast_BtOnTitle"] = "Bluetooth Turned On";
        _idStrings["Toast_BtOnSub"] = "Radio Bluetooth menyala & siap menghubungkan perangkat";
        _enStrings["Toast_BtOnSub"] = "Bluetooth radio is on and ready to connect";
        _idStrings["Toast_BtOffTitle"] = "Bluetooth Dimatikan";
        _enStrings["Toast_BtOffTitle"] = "Bluetooth Turned Off";
        _idStrings["Toast_BtOffSub"] = "Radio Bluetooth dimatikan untuk menghemat baterai";
        _enStrings["Toast_BtOffSub"] = "Bluetooth radio turned off to conserve battery";
        _idStrings["Toast_WifiConnectedTitle"] = "Wi-Fi Terhubung";
        _enStrings["Toast_WifiConnectedTitle"] = "Wi-Fi Connected";
        _idStrings["Toast_WifiConnectedSub"] = "Koneksi ke {0} aktif";
        _enStrings["Toast_WifiConnectedSub"] = "Connected to {0}";
        _idStrings["Toast_WifiDisconnectedTitle"] = "Wi-Fi Terputus";
        _enStrings["Toast_WifiDisconnectedTitle"] = "Wi-Fi Disconnected";
        _idStrings["Toast_WifiDisconnectedSub"] = "Tidak ada koneksi jaringan aktif";
        _enStrings["Toast_WifiDisconnectedSub"] = "No active network connection";
        _idStrings["Toast_CapsLockTitle"] = "Caps Lock";
        _enStrings["Toast_CapsLockTitle"] = "Caps Lock";
        _idStrings["Toast_CapsLockSubOn"] = "Huruf kapital aktif (ON)";
        _enStrings["Toast_CapsLockSubOn"] = "Uppercase lock enabled (ON)";
        _idStrings["Toast_CapsLockSubOff"] = "Huruf kapital nonaktif (OFF)";
        _enStrings["Toast_CapsLockSubOff"] = "Uppercase lock disabled (OFF)";
        _idStrings["Toast_MicInUse"] = "Mikrofon Sedang Digunakan";
        _enStrings["Toast_MicInUse"] = "Microphone In Use";
        _idStrings["Toast_CamInUse"] = "Kamera Sedang Digunakan";
        _enStrings["Toast_CamInUse"] = "Camera In Use";
        _idStrings["Toast_AppUsingDevice"] = "{0} sedang mengakses perangkat ini";
        _enStrings["Toast_AppUsingDevice"] = "{0} is accessing this device";
        _idStrings["Perf_UptimeFmt"] = "{0}j {1}m";
        _enStrings["Perf_UptimeFmt"] = "{0}h {1}m";
        _idStrings["Perf_UptimeMinFmt"] = "{0}m {1}d";
        _enStrings["Perf_UptimeMinFmt"] = "{0}m {1}s";

        // Notifications & Pomodoro & Weather
        _idStrings["Notif_NewFallback"] = "Notifikasi baru";
        _enStrings["Notif_NewFallback"] = "New notification";
        _idStrings["Notif_IncomingTitle"] = "Pesan Masuk";
        _enStrings["Notif_IncomingTitle"] = "Incoming Message";
        _idStrings["Notif_SystemFallback"] = "Notifikasi Sistem";
        _enStrings["Notif_SystemFallback"] = "System Notification";
        _idStrings["Time_JustNow"] = "Baru saja";
        _enStrings["Time_JustNow"] = "Just now";
        _idStrings["Time_MinutesAgo"] = "{0}m lalu";
        _enStrings["Time_MinutesAgo"] = "{0}m ago";
        _idStrings["Time_HoursAgo"] = "{0}j lalu";
        _enStrings["Time_HoursAgo"] = "{0}h ago";

        _idStrings["Pomo_FocusDoneTitle"] = "Sesi Fokus Selesai!";
        _enStrings["Pomo_FocusDoneTitle"] = "Focus Session Complete!";
        _idStrings["Pomo_FocusDoneSub"] = "Waktunya istirahat {0} menit.";
        _enStrings["Pomo_FocusDoneSub"] = "Time for a {0}-minute break.";
        _idStrings["Pomo_BreakDoneTitle"] = "Istirahat Selesai!";
        _enStrings["Pomo_BreakDoneTitle"] = "Break Complete!";
        _idStrings["Pomo_BreakDoneSub"] = "Siap memulai sesi fokus baru.";
        _enStrings["Pomo_BreakDoneSub"] = "Ready to start a new focus session.";
        _idStrings["Pomo_ModeTimer"] = "Timer Fokus";
        _enStrings["Pomo_ModeTimer"] = "Focus Timer";
        _idStrings["Pomo_ModeStopwatch"] = "Stopwatch";
        _enStrings["Pomo_ModeStopwatch"] = "Stopwatch";
        _idStrings["Pomo_StopwatchStart"] = "Mulai Stopwatch";
        _enStrings["Pomo_StopwatchStart"] = "Start Stopwatch";
        _idStrings["Pomo_StopwatchLap"] = "Putaran";
        _enStrings["Pomo_StopwatchLap"] = "Lap";

        _idStrings["Weather_Clear"] = "Cerah";
        _enStrings["Weather_Clear"] = "Clear";
        _idStrings["Weather_PartlyCloudy"] = "Cerah Berawan";
        _enStrings["Weather_PartlyCloudy"] = "Partly Cloudy";
        _idStrings["Weather_Cloudy"] = "Berawan";
        _enStrings["Weather_Cloudy"] = "Cloudy";
        _idStrings["Weather_Fog"] = "Berkabut";
        _enStrings["Weather_Fog"] = "Foggy";
        _idStrings["Weather_Drizzle"] = "Gerimis";
        _enStrings["Weather_Drizzle"] = "Drizzle";
        _idStrings["Weather_Rain"] = "Hujan";
        _enStrings["Weather_Rain"] = "Rain";
        _idStrings["Weather_HeavyRain"] = "Hujan Lebat";
        _enStrings["Weather_HeavyRain"] = "Heavy Rain";
        _idStrings["Weather_Snow"] = "Salju";
        _enStrings["Weather_Snow"] = "Snow";
        _idStrings["Weather_Thunderstorm"] = "Badai Petir";
        _enStrings["Weather_Thunderstorm"] = "Thunderstorm";
        _idStrings["Weather_Mixed"] = "Berawan / Cerah";
        _enStrings["Weather_Mixed"] = "Mixed Clouds";

        // Lyrics & Spotlight & New Tools (Audio Switcher, OCR, Eyedropper, QR Share, Auto-Updater)
        _idStrings["Lyrics_NotFound"] = "Lirik tidak ditemukan untuk lagu ini";
        _enStrings["Lyrics_NotFound"] = "Lyrics not found for this track";
        _idStrings["Lyrics_Fetching"] = "Mencari lirik lagu...";
        _enStrings["Lyrics_Fetching"] = "Searching for lyrics...";
        _idStrings["Lyrics_NoTiming"] = "Lirik tersedia (tanpa timestamp)";
        _enStrings["Lyrics_NoTiming"] = "Static lyrics loaded (no timestamps)";
        _idStrings["Spotlight_ShortcutApp"] = "Aplikasi Pintasan";
        _enStrings["Spotlight_ShortcutApp"] = "Shortcut App";
        _idStrings["Spotlight_WindowsApp"] = "Aplikasi Windows";
        _enStrings["Spotlight_WindowsApp"] = "Windows App";
        _idStrings["Spotlight_Placeholder"] = "Cari aplikasi, hitung cepat, ocr, picker, sleep...";
        _enStrings["Spotlight_Placeholder"] = "Search apps, calculate, ocr, picker, sleep...";
        _idStrings["Audio_SwitchTitle"] = "Pilih Output Audio";
        _enStrings["Audio_SwitchTitle"] = "Select Audio Output";
        _idStrings["Audio_SwitchedToast"] = "Output Audio Dialihkan";
        _enStrings["Audio_SwitchedToast"] = "Audio Output Switched";
        _idStrings["Ocr_CopySuccess"] = "Teks Layar Disalin (OCR)";
        _enStrings["Ocr_CopySuccess"] = "Screen Text Copied (OCR)";
        _idStrings["Ocr_NoTextFound"] = "Tidak ada teks terdeteksi di area ini";
        _enStrings["Ocr_NoTextFound"] = "No readable text found in selection";
        _idStrings["Ocr_BtnLabel"] = "OCR Teks";
        _enStrings["Ocr_BtnLabel"] = "OCR Text";
        _idStrings["Picker_CopySuccess"] = "Warna Disalin ({0})";
        _enStrings["Picker_CopySuccess"] = "Color Copied ({0})";
        _idStrings["Picker_BtnLabel"] = "Ambil Warna";
        _enStrings["Picker_BtnLabel"] = "Color Picker";
        _idStrings["Qr_ShareBtn"] = "Bagikan QR";
        _enStrings["Qr_ShareBtn"] = "QR Share";
        _idStrings["Qr_Title"] = "Berbagi Wi-Fi Lokal";
        _enStrings["Qr_Title"] = "Local Wi-Fi Share";
        _idStrings["Qr_ScanHint"] = "Pindai QR dengan kamera HP pada Wi-Fi yang sama";
        _enStrings["Qr_ScanHint"] = "Scan QR with your phone camera on the same Wi-Fi";
        _idStrings["Qr_StopBtn"] = "Hentikan Server";
        _enStrings["Qr_StopBtn"] = "Stop Server";
        _idStrings["Updater_Checking"] = "Memeriksa versi terbaru...";
        _enStrings["Updater_Checking"] = "Checking for latest release...";
        _idStrings["Updater_UpToDate"] = "Anda menggunakan versi terbaru ({0})";
        _enStrings["Updater_UpToDate"] = "You are on the latest version ({0})";
        _idStrings["Updater_Available"] = "Tersedia pembaruan v{0}!";
        _enStrings["Updater_Available"] = "Update v{0} available!";
        _idStrings["Updater_Downloading"] = "Mengunduh pembaruan... {0}%";
        _enStrings["Updater_Downloading"] = "Downloading update... {0}%";
        _idStrings["Updater_Installing"] = "Memasang pembaruan & memulai ulang...";
        _enStrings["Updater_Installing"] = "Installing update & restarting...";
    }
}
