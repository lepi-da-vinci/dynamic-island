<#
.SYNOPSIS
    Windows Dynamic Island - One-Click Installer & Runner Script
.DESCRIPTION
    Builds the solution in Release mode, creates an optional Desktop shortcut, and launches the application.
#>

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Windows Dynamic Island - Quick Installer / Launcher " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Check for .NET SDK
Write-Host "[1/4] Checking .NET SDK..." -ForegroundColor Yellow
if (-not (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    Write-Host "[ERROR] .NET SDK is not installed or not in PATH!" -ForegroundColor Red
    Write-Host "Please download and install .NET SDK (version 10 or 9) from:" -ForegroundColor White
    Write-Host "https://dotnet.microsoft.com/download" -ForegroundColor Cyan
    exit 1
}

$dotnetVer = dotnet --version
Write-Host "  -> Found .NET SDK version: $dotnetVer" -ForegroundColor Green

# 2. Stop running instances
Write-Host "[2/4] Terminating any existing Dynamic Island instance..." -ForegroundColor Yellow
Stop-Process -Name "WindowsDynamicIsland" -Force -ErrorAction SilentlyContinue

# 3. Build Release binary
$projPath = Join-Path $PSScriptRoot "src\WindowsDynamicIsland\WindowsDynamicIsland.csproj"
if (-not (Test-Path $projPath)) {
    Write-Host "[ERROR] Project file not found at: $projPath" -ForegroundColor Red
    exit 1
}

Write-Host "[3/4] Building WindowsDynamicIsland in Release mode..." -ForegroundColor Yellow
dotnet build "$projPath" -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] Build failed! Check compiler output above." -ForegroundColor Red
    exit $LASTEXITCODE
}

# Find built executable
$binDir = Join-Path $PSScriptRoot "src\WindowsDynamicIsland\bin\Release"
$targetExe = Get-ChildItem -Path $binDir -Filter "WindowsDynamicIsland.exe" -Recurse | Select-Object -First 1

if (-not $targetExe -or -not (Test-Path $targetExe.FullName)) {
    Write-Host "[ERROR] Compiled executable not found!" -ForegroundColor Red
    exit 1
}

Write-Host "  -> Compiled binary: $($targetExe.FullName)" -ForegroundColor Green

# 4. Optional: Create Desktop Shortcut
$desktopPath = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktopPath "Windows Dynamic Island.lnk"

try {
    $wshShell = New-Object -ComObject WScript.Shell
    $shortcut = $wshShell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $targetExe.FullName
    $shortcut.WorkingDirectory = $targetExe.DirectoryName
    $shortcut.Description = "Windows Dynamic Island"
    $shortcut.Save()
    Write-Host "  -> Desktop shortcut created: $shortcutPath" -ForegroundColor Green
} catch {
    # Ignore shortcut creation failure
}

# 5. Launch Application
Write-Host "[4/4] Starting Windows Dynamic Island..." -ForegroundColor Yellow
Start-Process -FilePath $targetExe.FullName -WorkingDirectory $targetExe.DirectoryName

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  [SUCCESS] Windows Dynamic Island is now running at the top of your screen!" -ForegroundColor Green
Write-Host "  Tip: Hover mouse to expand, Right-Click to cycle tabs, Scroll to change volume." -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Green

