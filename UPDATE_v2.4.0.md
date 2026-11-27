# Catatan Pembaruan Windows Dynamic Island v2.4.0

Dokumen ini merangkum seluruh perbaikan bug, fitur baru integrasi perangkat keras, dan peningkatan kualitas pengalaman pengguna (UX) yang diterapkan pada rilis **v2.4.0**.

---

## 1. Perbaikan Kecerahan Layar (Brightness OSD & AC/DC Policy)
- **Membangunkan Dynamic Island dari Mode Simpan (`TuckedTopEdge`)**:
  - Sebelumnya `_brightnessService.PollExternalChanges()` di `MainWindow.xaml.cs` dilewati ketika Dynamic Island sedang terlipat di tepi atas layar, sehingga menekan tombol kecerahan keyboard fisik (`F4` / `F5`) tidak memunculkan Toast OSD.
  - Kini pemantauan perubahan kecerahan dieksekusi secara konsisten pada setiap siklus *fast-poll* sebagaimana tombol volume, sehingga menekan tombol kecerahan fisik selalu menurunkan Dynamic Island dan menampilkan OSD slider kecerahan secara instan.
- **Perbaikan Pembacaan `DISPLAYPOLICY_BOTH` (`\\.\LCD`)**:
  - Di `BrightnessService.cs`, ketika laptop terhubung ke pengisi daya (AC Power) dengan `ucDisplayPolicy == 3` (`DISPLAYPOLICY_BOTH`), sebelumnya logika pembacaan memilih `ucDCBrightness` (nilai mode baterai) yang tidak ikut berubah saat kecerahan mode AC diubah.
  - Menambahkan metode `ResolveBrightnessFromPolicy(...)` dengan pengecekan status daya `GetSystemPowerStatus` (`ACLineStatus`) sehingga nilai kecerahan selalu akurat baik saat menggunakan baterai maupun saat di-cas.

---

## 2. Sinkronisasi Real-Time & Scroll Wheel di Jendela Settings
- **Sinkronisasi Dua Arah Instan**:
  - Seluruh event `SpeakerVolumeOrMuteChanged`, `MicrophoneMuteChanged`, `OutputDeviceChanged`, `BrightnessChanged`, geseran slider pada Toast Dynamic Island, serta siklus telemetri 1 detik kini langsung menyegarkan kartu **System Control Center** di `Settings -> General` (`RefreshSystemControlCenterCard()`).
  - Menambahkan pengaman `!_isDraggingVol` dan `!_isDraggingBrt` agar pembaruan latar belakang tidak pernah mengganggu interaksi geser mouse pengguna di dalam jendela Settings.
- **Dukungan Mouse Scroll Wheel pada Kartu Volume & Kecerahan**:
  - Pengguna kini dapat langsung memutar roda scroll mouse (`+/- 2%` per takik) tepat di atas kartu **Speaker** maupun **Kecerahan Layar** di jendela Settings tanpa perlu menahan klik.

---

## 3. Notifikasi OSD Touchpad ON / OFF (`F6`)
- **Deteksi Native Windows Precision TouchPad**:
  - `HardwareTelemetryService.PollFastKeyboardIndicators()` memantau perubahan status registri `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status` (`Enabled`) secara *real-time* tanpa proses eksternal.
  - Menekan tombol pintas Touchpad (`F6` pada ASUS Vivobook atau laptop Windows 10/11 lainnya) langsung menampilkan Toast OSD **Touchpad: ON (Aktif)** atau **Touchpad: OFF (Nonaktif)** lengkap dengan ikon vektor khusus (`GeomTouchpadOn` dan `GeomTouchpadOff`).

---

## 4. Notifikasi OSD Lampu Latar Keyboard / Keyboard Backlight (`F7`)
- **Integrasi Native Win32 `\\.\ATKACPI` (Tanpa Admin & Tanpa PowerShell)**:
  - Membaca level lampu latar keyboard ASUS secara langsung melalui `DeviceIoControl` pada driver kernel `\\.\ATKACPI` (`DSTS` device ID `0x00050021`). Pada perangkat non-ASUS, pemeriksaan ini otomatis menonaktifkan diri setelah percobaan pertama sehingga menghasilkan *zero overhead*.
  - Menekan tombol lampu keyboard (`F7`) menampilkan Toast OSD **Lampu Keyboard** dengan 4 tingkat kecerahan (`Mati`, `Redup (1/3)`, `Sedang (2/3)`, `Terang (3/3)`), indikator progres proporsional, dan ikon vektor `GeomKeyboardBacklight`.

---

## 5. Peningkatan Tambahan Ekosistem (v2.4.0 Polish)
- **Akurasi Lokasi Cuaca Otomatis berbasis Windows Geolocator (Wi-Fi / GPS)**:
  - `WeatherService.ResolveLocationAsync()` kini memprioritaskan `Windows.Devices.Geolocation.Geolocator` dengan *reverse geocoding* lokal sebelum jatuh ke *fallback* IP Geolocation (`ip-api.com`), mengatasi masalah kota yang meleset akibat routing gateway ISP.
- **Perintah Cepat Volume & Kecerahan di Spotlight (`Alt + Spasi`)**:
  - Menambahkan perintah instan di Spotlight Command Palette: ketik `vol 50`, `suara 75`, `brt 80`, atau `kecerahan 40` lalu tekan `Enter` untuk mengatur tingkat volume atau kecerahan layar secara presisi tanpa menyentuh mouse.
- **Lokalisasi & Pengujian Unit (`WindowsDynamicIsland.Tests`)**:
  - Seluruh teks baru tersedia dalam Bahasa Indonesia dan Bahasa Inggris di `LocalizationService.cs`.
  - Menambahkan unit test baru untuk kebijakan kecerahan AC/DC (`BrightnessService_ResolveBrightnessFromPolicy_SelectsAcOrDcCorrectly`) dan dekoder level lampu keyboard (`HardwareTelemetryService_ParseAsusBacklightLevel_DecodesPresentBitAndLevels`).
- **Kepatuhan Penuh Standar Desain Apple UI/UX**:
  - Seluruh ikon baru didefinisikan sebagai *bespoke SVG Path Geometry* di `App.xaml` tanpa penggunaan karakter emoji unicode pada kode maupun dokumentasi.
