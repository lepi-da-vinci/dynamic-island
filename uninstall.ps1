<#
.SYNOPSIS
    Windows Dynamic Island - Official Uninstaller Script
.DESCRIPTION
    Stops Windows Dynamic Island, removes Startup registration, removes Desktop and Start Menu shortcuts,
    removes Windows Installed Apps registration, and deletes the installation folder.
#>

$ErrorActionPreference = "SilentlyContinue"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Windows Dynamic Island - Official Uninstaller           " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Stop running processes
Write-Host "[1/4] Stopping Windows Dynamic Island..." -ForegroundColor Yellow
Stop-Process -Name "WindowsDynamicIsland" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "WindowsDynamicIsland-Setup" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "Uninstall" -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

# 2. Remove Registry entries (Startup & Installed Apps)
Write-Host "[2/4] Removing Startup and Windows Installed Apps registry entries..." -ForegroundColor Yellow
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "WindowsDynamicIsland" -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsDynamicIsland" -Recurse -Force -ErrorAction SilentlyContinue

# 3. Remove Desktop & Start Menu shortcuts
Write-Host "[3/4] Removing Desktop and Start Menu shortcuts..." -ForegroundColor Yellow
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "Windows Dynamic Island.lnk"
if (Test-Path $desktopShortcut) { Remove-Item $desktopShortcut -Force }

$programsDir = [Environment]::GetFolderPath("Programs")
$smRootShortcut = Join-Path $programsDir "Windows Dynamic Island.lnk"
if (Test-Path $smRootShortcut) { Remove-Item $smRootShortcut -Force }

$smFolder = Join-Path $programsDir "Windows Dynamic Island"
if (Test-Path $smFolder) { Remove-Item $smFolder -Recurse -Force }

# 4. Remove installation folder
Write-Host "[4/4] Removing application files..." -ForegroundColor Yellow
$installDir = Join-Path $env:LOCALAPPDATA "Programs\WindowsDynamicIsland"
if (Test-Path $installDir) {
    Remove-Item -Path $installDir -Recurse -Force -ErrorAction SilentlyContinue
}

$legacyInstallDir = Join-Path $env:LOCALAPPDATA "WindowsDynamicIsland"
if (Test-Path $legacyInstallDir) {
    Remove-Item -Path $legacyInstallDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  [SUCCESS] Windows Dynamic Island has been completely uninstalled." -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green

