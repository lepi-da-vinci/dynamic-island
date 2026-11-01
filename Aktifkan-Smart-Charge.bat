@echo off
title Windows Dynamic Island - Aktivasi Kontrol Hardware Baterai
echo ==============================================================
echo   Windows Dynamic Island - Setup Kontrol Baterai Motherboard
echo ==============================================================
echo.
echo Mendaftarkan tugas sistem agar pengubahan batas daya (80%% / 100%%)
echo dapat berjalan 100%% senyap tanpa popup Windows...
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup_battery_tasks.ps1"
echo.
pause

