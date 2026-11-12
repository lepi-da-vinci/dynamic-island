namespace WindowsDynamicIsland.Services;

/// <summary>
/// Centralized Apple-standard localization engine supporting Bahasa Indonesia ("id") and English ("en").
/// Provides dynamic UI re-translation across MainWindow and SettingsWindow without restarting the application.
/// </summary>
public sealed class LocalizationService
{
    private static readonly Lazy<LocalizationService> _lazyInstance = new(() => new LocalizationService());
    public static LocalizationService Instance => _lazyInstance.Value;

    private string _currentLanguage = "id";
    public string CurrentLanguage => _currentLanguage;
    public bool IsEnglish => _currentLanguage.Equals("en", StringComparison.OrdinalIgnoreCase);

    public event Action? LanguageChanged;

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
        LanguageChanged?.Invoke();
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
        _idStrings["Settings_Language"] = "Interface Language";
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
    }
}
