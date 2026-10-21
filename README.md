# 🏝️ Windows Dynamic Island: Technical Blueprint & Architecture Guide

Sebuah panduan komprehensif untuk merancang dan membangun aplikasi **Dynamic Island** native, ultra-ringan, dan responsif untuk Windows 10 & 11.

---

## 1. Arsitektur & Filosofi Sistem

Agar Dynamic Island terasa menyatu dengan OS (seperti di iOS), aplikasi harus memenuhi empat prinsip utama:
1. **Ultra Low-Overhead:** Penggunaan RAM < 30–50 MB dan CPU 0% saat idle.
2. **Fluid Physics & Dynamic Resizing:** Animasi perubahan ukuran (*pill morphing*) berbasis fisika *spring/cubic-bezier* 60–120 FPS tanpa patah (*stutter*).
3. **Non-Intrusive Overlay:** Selalu berada di lapisan paling atas (*Topmost*), tidak mencuri fokus keyboard/mouse dari aplikasi utama pengguna (`WS_EX_NOACTIVATE`), dan ukuran/area klik menyesuaikan bentuk pulau secara dinamis.
4. **Context-Aware (Sadar Konteks):** Otomatis bersembunyi atau menjadi *click-through* saat pengguna bermain game *full-screen* atau menonton video layar penuh.

---

## 2. Pilihan Tech Stack Native

Untuk performa tertinggi dan integrasi sistem terdalam, ada dua jalur utama:

| Komponen | Jalur A: C# / .NET 8/9 (Native AOT / WPF-Win32 Interop) ⭐ *Direkomendasikan* | Jalur B: C++ / Win32 & DirectComposition (Purist) |
| :--- | :--- | :--- |
| **Kecepatan Dev** | Cepat, kode modern, banyak library wrapper Windows Runtime (`CsWinRT`, `NAudio`/`Vortice`). | Sedang - Lambat, pengelolaan memori manual. |
| **Performa & RAM**| Sangat cepat (~25–45 MB RAM). | Paling ringan (~10–20 MB RAM, hampir zero-runtime). |
| **Windowing/UI** | Win32 Custom Window + DirectComposition / WinUI 3 / WPF Hardware Accelerated. | Win32 API murni + Direct2D / Windows Composition. |
| **Animasi** | `Windows.UI.Composition` (GPU-accelerated hardware compositor). | DirectComposition / Composition APIs. |

> **Rekomendasi:** Gunakan **C# dengan .NET 8/9** dipadukan dengan **Win32 P/Invoke + Windows Composition APIs**. Pendekatan ini memberi kemudahan mengonsumsi Windows SDK Runtime (`Windows.Media.Control`, `Windows.Devices`) tanpa mengorbankan konsumsi RAM dan kehalusan animasi.

---

## 3. Integrasi Modul & Windows API

### A. Media Controller & Real-Time Audio Visualizer
1. **Metadata & Kontrol Media (GSMTC):**
   * **API:** `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager` (GSMTC).
   * **Fungsi:** Mengetahui lagu yang sedang diputar (Judul, Artis, Album Art), status playback (`Playing`, `Paused`), timeline posisi lagu, serta mengirim kontrol balik (`Play`, `Pause`, `Next`, `Previous`, `Seek`).
   * **Kelebihan:** Otomatis mendukung Spotify Desktop, YouTube di Chrome/Edge/Firefox, foobar2000, Apple Music, VLC, dsb.
2. **Real-Time Audio Spectrum / Waveform (WASAPI Loopback):** *(Tambahan Baru)*
   * GSMTC tidak mengirimkan frekuensi audio. Agar *waveform* mini bergerak sesuai ketukan bass/musik asli (bukan animasi statis):
   * **API:** Windows Core Audio **WASAPI Loopback (`IAudioCaptureClient`)** + Fast Fourier Transform (FFT) ringan menggunakan `Span<float>` (zero-allocation).
3. **Dynamic Color Extraction (Adaptive Glow):** *(Tambahan Baru)*
   * Mengekstrak warna dominan dari *thumbnail stream* Album Art GSMTC untuk mewarnai bar *waveform* dan efek pendaran (*ambient glow*) sesuai sampul lagu yang sedang diputar.

### B. Sinkronisasi Lirik (Lyrics Engine + Local Cache)
Karena Windows GSMTC tidak menyediakan payload lirik lagu:
* **Solusi:** Gunakan API publik open-source gratis dan sangat cepat **[LRCLIB API](https://lrclib.net/)** dengan dukungan **Local Disk Cache**.
* **Alur:**
  1. Saat GSMTC memicu event lagu baru (`MediaPropertiesChanged`), cek terlebih dahulu cache lokal di `%LocalAppData%\WindowsDynamicIsland\lyrics\`.
  2. Jika belum ada, lakukan request ke `https://lrclib.net/api/get?artist_name=...&track_name=...`.
  3. Ambil field `syncedLyrics` (format `.lrc` standar: `[mm:ss.xx] baris lirik`) dan simpan ke cache lokal agar hemat kuota dan bisa diakses offline.
  4. Cocokkan waktu lagu saat ini (`TimelineProperties.Position` + interpolasi waktu) dengan timestamp lirik, lalu tampilkan teks dengan animasi transisi baris per baris.

### C. Status Perangkat Keras & Jaringan
* **Baterai:** Win32 API `GetSystemPowerStatus` atau `Windows.Devices.Power.Battery`. Tangkap status persentase, charging (AC connected), dan mode hemat daya (*Battery Saver*).
* **Bluetooth:** `Windows.Devices.Bluetooth.BluetoothAdapter` & `Windows.Devices.Enumeration.DeviceWatcher` untuk mendeteksi earphone/mouse/gamepad saat terhubung beserta level baterainya.
* **Wi-Fi & Network:** `Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()` untuk mendeteksi SSID, kekuatan sinyal, atau status disconnect.
* **Volume, Mic Mute & Brightness:**
  * Windows Core Audio APIs (`IAudioEndpointVolume`) untuk mengganti OSD volume bawaan Windows 10/11 dengan tampilan pil yang jauh lebih elegan, ditambah fitur **Global Mic Mute Toggle** beserta indikator lampu ketika mikrofon sedang aktif dipakai.
  * **DDC/CI (`SetMonitorBrightness`) & WMI** untuk mengatur kecerahan layar baik di laptop maupun monitor desktop eksternal.

### D. Penanganan Kasus Khusus OS (Windows Edge Cases) *(Tambahan Baru)*
* **Full-Screen Game & Video Detection:** Menggunakan `SHQueryUserNotificationState` (`QUNS_RUNNING_D3D_FULL_SCREEN` / `QUNS_PRESENTATION_MODE`) agar Dynamic Island otomatis tersembunyi (*auto-hide*) saat pengguna bermain game kompetitif atau menonton film *full-screen*.
* **Dynamic Hit-Testing (`WM_NCHITTEST`):** Area kanvas jendela harus lebih besar dari ukuran pil untuk menampung animasi *spring bounce* dan bayangan (*drop shadow*), tetapi area transparan di luar pil wajib mengembalikan `HTTRANSPARENT` agar **tidak memblokir klik mouse ke tab browser** di belakangnya.
* **Multi-Monitor & Per-Monitor DPI (`PerMonitorV2`):** Mendukung perpindahan layar otomatis atau pemilihan monitor target dengan scaling tajam di resolusi 100%, 125%, 150%, hingga 200% (4K).

---

## 4. Ide Fitur Produktivitas Khusus Pengguna PC

Untuk membuat aplikasi ini jauh lebih berguna di Windows daripada sekadar tiruan visual iOS:

1. **File Drop Shelf / Temporary Tray ("AirDrop-style" Stash):** *(Fitur Unggulan Baru)*
   * Saat pengguna men-*drag* file, gambar, atau potongan teks ke arah atas layar, Dynamic Island otomatis membuka **Rak Penyimpanan Sementara (Dropzone)**. Pengguna bisa menyimpan beberapa file di sana, berpindah ke aplikasi/folder lain (misal WhatsApp, Discord, Email, Explorer), lalu men-*drag* keluar file tersebut tanpa harus membuka dua jendela Explorer berdampingan.
2. **Camera & Microphone Privacy Indicator + Quick Mute:**
   * Menampilkan titik hijau (kamera) / oranye (mikrofon) saat ada aplikasi (Zoom, Discord, Browser) yang mengakses perangkat, lengkap dengan kemampuan **klik titik oranye untuk langsung Mute/Unmute mikrofon secara global**.
3. **Mouse Gesture & Scroll Wheel Actions:** *(Fitur Unggulan Baru)*
   * *Scroll Wheel* di atas pulau: Mengatur volume sistem secara instan (atau *seek* durasi lagu).
   * *Middle Click (Klik Tengah)*: Play/Pause musik instan tanpa perlu memperluas pulau.
   * *Swipe / Klik Kanan*: Beralih halaman mode pulau (Media ➔ File Shelf ➔ Pomodoro Timer ➔ System Monitor).
4. **Clipboard Peek & History Mini:**
   * Ketika menekan `Ctrl + C`, pulau memantulkan animasi mini *"Copied: [Potongan Teks / Thumbnail Gambar]"* dan menyimpan 5 item clipboard terakhir untuk di-paste cepat.
5. **Caps Lock, Num Lock & Keyboard Layer HUD:**
   * Bagi pengguna keyboard mekanikal custom (60%/65%/75%) atau laptop tanpa lampu LED indikator, pulau membesar sebentar menampilkan status **"CAPS ON"** / **"CAPS OFF"**.
6. **Mini Pomodoro / Focus Timer / Alarm:**
   * Mode jam default yang bisa di-scroll menjadi timer fokus Pomodoro 25 menit dengan ring progress melingkar di sekeliling pil.
7. **System Telemetry Ringkas (CPU / GPU / Network Speed):**
   * Opsi mode *compact* untuk menampilkan suhu/beban CPU & GPU atau kecepatan unduh jaringan secara minimalis.
8. **Webcam Notch Adaptor vs Floating Pill:**
   * Pilihan mode menempel di bibir atas layar (*Notch Mode*) atau mengambang beberapa piksel dari atas (*Floating Pill Mode* seperti iPhone 15/16 Pro) yang otomatis bergeser jika pengguna menaruh kursor di tab paling atas browser (dengan *Alt+Hover* untuk *bypass*).

---

## 5. Roadmap Langkah demi Langkah (Implementation Plan)

```
[Fase 1: Window Foundation, DPI, & Dynamic Hit-Testing]
         │
         ▼
[Fase 2: Spring Animation, Morphing Engine & Tray Icon]
         │
         ▼
[Fase 3: Windows Media (GSMTC), WASAPI Waveform & Lirik (LRCLIB)]
         │
         ▼
[Fase 4: Telemetri Sistem, Audio/Brightness OSD & Privacy Indicator]
         │
         ▼
[Fase 5: Produktivitas PC (File Drop Shelf, Clipboard Peek, Pomodoro)]
         │
         ▼
[Fase 6: Full-Screen Game Detection, Settings UI & Zero-Overhead Polishing]
```

### Langkah 1: Pembuatan Overlay Window (Transparan, Topmost & Smart Click-Through)
* Buat Win32 Window dengan flag:
  * `WS_EX_TOPMOST`: Selalu di atas window lain.
  * `WS_EX_TOOLWINDOW`: Tidak muncul di daftar `Alt + Tab`.
  * `WS_EX_LAYERED` / DirectComposition: Mendukung transparansi per-pixel anti-aliasing.
  * `WS_EX_NOACTIVATE`: Tidak mengambil fokus game/aplikasi yang sedang aktif saat disentuh.
* Terapkan *Dynamic Hit-Testing* (`WM_NCHITTEST`): Hanya area di dalam geometri pil saat ini yang menerima input mouse; area luar kanvas meneruskan klik ke aplikasi di bawahnya (`HTTRANSPARENT`).

### Langkah 2: State Machine & Multi-Widget Pager untuk Island
Rancang pulau dengan beberapa mode (*States*) dan prioritas interupsi:
* `State: Hidden`: Saat aplikasi Full-Screen / Game terdeteksi.
* `State: Minimized (Idle Pill)`: Ukuran ~150x32 px (Jam, indikator privasi kamera/mic, atau telemetri mini).
* `State: Compact Activity`: Ukuran ~230x36 px (Thumbnail album berputar/statis + WASAPI Audio Waveform mini, atau countdown timer).
* `State: Notification Toast (Interrupt)`: Membesar sementara selama 2–3 detik (Caps Lock, Bluetooth connect, Charger, Clipboard copied, Volume slider).
* `State: Expanded Dashboard`: Ukuran ~390x165 px (Kontrol musik lengkap + lirik berjalan, File Drop Shelf, atau Pomodoro Timer).

### Langkah 3: Integrasi GSMTC, WASAPI & LRCLIB (Contoh Logika C#)

```csharp
// Inisialisasi GSMTC
var sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
var currentSession = sessionManager.GetCurrentSession();

if (currentSession != null)
{
    var mediaProperties = await currentSession.TryGetMediaPropertiesAsync();
    string title = mediaProperties.Title;
    string artist = mediaProperties.Artist;
    
    // Cek cache lokal terlebih dahulu, lalu ambil lirik dari LRCLIB secara asynchronous
    using var client = new HttpClient();
    string url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
    var response = await client.GetStringAsync(url);
    // Parse JSON dan simpan format syncedLyrics ke cache
}
```

### Langkah 4: Animasi Super Halus (Physics Spring Engine)
* Gunakan kurva fisika **Spring Damper (Underdamped / Critically Damped Spring)** untuk perubahan lebar (`Width`), tinggi (`Height`), dan radius sudut (`CornerRadius`).
* Saat transisi dari *Compact* ke *Expanded*, berikan sedikit efek *overshoot* elastis khas Dynamic Island agar terasa hidup dan responsif terhadap arah datangnya kursor.

### Langkah 5: Efisiensi Baterai & Resource (Low Footprint)
* **Event-Driven:** Jangan lakukan *polling* agresif. Gunakan Windows Event Subscriptions (`Battery.ReportUpdated`, `SessionManager.CurrentSessionChanged`, `UserNotificationListener`).
* **Frame Rate Throttling (0 FPS Idle):** Hentikan loop render (0 FPS) saat pulau dalam kondisi statis/idle. Aktifkan 60–120 FPS hanya ketika terjadi animasi morphing, kursor berada di atas pulau, atau saat audio visualizer aktif.

---

## 6. Struktur Project (Implemented)

```text
WindowsDynamicIsland/
├── src/
│   └── WindowsDynamicIsland/
│       ├── Core/
│       │   ├── Win32Interop.cs              # Win32 WS_EX_NOACTIVATE, WM_NCHITTEST, Tray, Full-Screen Detector
│       │   ├── IslandStateMachine.cs        # State: Hidden, IdlePill, CompactMedia, Toast, ExpandedDashboard
│       │   └── SpringPhysics.cs             # Second-order damped harmonic oscillator (0 FPS idle throttling)
│       ├── Services/
│       │   ├── MediaSessionService.cs       # GSMTC Windows API & Album Art Vibrant Color Extractor
│       │   ├── AudioService.cs              # Native Windows Core Audio (WASAPI Peak Meter & Master Volume)
│       │   ├── LyricsService.cs             # LRCLIB API client, LRC parser & Local Disk Cache
│       │   ├── HardwareTelemetryService.cs  # Battery, Wi-Fi, CapsLock/NumLock, Webcam/Mic Privacy Monitor
│       │   ├── FileShelfService.cs          # Temporary Drag-and-Drop File Stash ("AirDrop" tray)
│       │   └── PomodoroTimerService.cs      # Focus 25m / Break 5m productivity countdown timer
│       ├── UI/
│       │   ├── MainWindow.xaml              # Multi-state Dynamic Island UI & 4-Tab Expanded Dashboard
│       │   └── MainWindow.xaml.cs           # Compositor loop, gesture handlers, WndProc hook
│       ├── App.xaml                         # Styles & single-instance mutex
│       ├── app.manifest                     # PerMonitorV2 DPI Awareness
│       └── WindowsDynamicIsland.csproj      # .NET 10 Windows SDK Target
├── WindowsDynamicIsland.slnx
└── README.md
```

---

## 7. Cara Menjalankan & Pintasan Interaksi (Quickstart)

### Menjalankan Aplikasi
```powershell
dotnet build WindowsDynamicIsland.slnx -c Release
dotnet run --project src/WindowsDynamicIsland/WindowsDynamicIsland.csproj -c Release
```

### Pintasan & Gestur Mouse di Atas Pulau
* **Hover Mouse:** Membuka *Expanded Dashboard* dengan animasi pegas (*Spring Morphing*).
* **Klik Kanan di Pulau:** Berpindah cepat antar 4 tab (*🎵 Musik & Lirik* ➔ *📂 File Shelf* ➔ *⏱️ Fokus Pomodoro* ➔ *⚡ Sistem*).
* **Klik Tengah (*Middle Click*):** Langsung *Play / Pause* media yang sedang berjalan.
* **Scroll Wheel di Atas Pulau:** Mengubah volume sistem Windows secara halus dengan *Toast OSD* dinamis.
* **Drag & Drop File ke Pulau:** Otomatis membuka tab **File Shelf** untuk menyimpan file sementara dan men-*drag* keluar ke aplikasi lain (WhatsApp, Discord, Browser, Explorer).
* **Tahan `Left Alt` + Klik:** Menembus pulau (*Click-Through Bypass*) untuk mengeklik tab browser tepat di belakang pulau.

