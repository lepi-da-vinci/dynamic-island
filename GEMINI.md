# Apple UI/UX Design System & Modern Aesthetic Standard

Aturan ini wajib dijalankan dan ditaati pada setiap sesi kerja, pembaruan kode, dan penambahan fitur pada antarmuka Windows Dynamic Island.

## 1. Prinsip Utama Desain Apple UI/UX (macOS / iOS HIG)
* **Siluet Organik & Kesatuan Elemen**: Seluruh komponen Dynamic Island harus menyatu dalam satu kontur notch yang kohesif, menggunakan kurva squircle/rounded corner yang mulus (CornerRadius 10-24). Hindari elemen melayang yang terputus atau bertabrakan.
* **Transparansi & Glassmorphism Gelap**: Latar belakang OLED ultra-dark (`#000000` hingga `#09090B`) dengan aksen kilau kaca tipis (`BorderBrush` antara `#15FFFFFF` hingga `#25FFFFFF`) dan efek bayangan lembut beradius tinggi (Drop Shadow blur 14-24, opacity 0.3-0.5).
* **Tipografi & Hirarki Visual**: Teks primer kontras tinggi (`#F8FAFC`), teks sekunder redup elegan (`#94A3B8`), dan teks tersier (`#64748B`). Aksen menggunakan palet sistem Apple: Cyan/Biru iOS (`#38BDF8`), Hijau Sukses (`#22C55E` / `#34D399`), Kuning Peringatan (`#FBBF24`), dan Merah Coral (`#FB7185`).

## 2. Standar Scrollbar & Overflow Konten
* **Floating Overlay Scrollbar**: Scrollbar tidak boleh mendorong atau menggeser konten saat muncul (selalu gunakan template overlay floating).
* **Kapsul Minimalis (Pill Thumb)**: Thumb scrollbar berbentuk kapsul ultra-ramping (lebar 5px saat diam, mengembang ke 7px saat hover) dengan sudut bulat penuh (`CornerRadius="2.5"`).
* **Tanpa Tombol Panah (No Arrow Buttons)**: Tombol panah atas/bawah wajib dihilangkan sepenuhnya.
* **Track Transparan**: Latar belakang track scrollbar harus 100% transparan tanpa garis batas kasar.
* **Gaya Standar Proyek**: Seluruh `ScrollViewer` wajib menggunakan atau mewarisi `AppleScrollViewerStyle` dan `AppleScrollBarStyle` dari `App.xaml`.

## 3. Mikro-Interaksi & Animasi
* **Fisika Halus & Responsif**: Transisi pelebaran, pemindahan tab, atau interaksi hover harus menggunakan kurva ease-out dinamis (CubicEase / QuinticEase).
* **Sayap Interaktif Sisi (Side Wings)**: Pergantian antar tab dilakukan melalui sayap sisi elastis yang menyatu secara organik dengan island utama saat kursor mendekat ke tepi.

## 4. Larangan Emoji & Ikon Vektor SVG
* **Zero Emojis**: Dilarang keras menggunakan emoji unicode pada UI, commit message, maupun dokumentasi.
* **Bespoke Vector Icons**: Semua ikon harus didefinisikan sebagai vektor SVG Path Geometries yang presisi, serasi, dan dapat di-scale dengan rapi.

## 5. Review Mandatori Sebelum Rilis
Setiap perubahan visual harus diperiksa kesesuaiannya dengan panduan estetika modern Apple ini sebelum di-compile, dipublikasikan, dan di-push ke GitHub.

