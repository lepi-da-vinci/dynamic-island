<#
.SYNOPSIS
    Windows Dynamic Island - One-Click Installer & Runner Script
.DESCRIPTION
    Installs and launches Windows Dynamic Island without SmartScreen warnings.
    Supports both local source build (if inside repository with .NET SDK) and
    direct standalone binary download from GitHub Releases (when run via irm | iex).
#>

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Windows Dynamic Island - Quick Installer / Launcher " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Stop any running instances and unblock Downloads folder binaries
Write-Host "[1/4] Preparing environment and stopping old instances..." -ForegroundColor Yellow
Stop-Process -Name "WindowsDynamicIsland" -Force -ErrorAction SilentlyContinue
try {
    Get-ChildItem -Path "$env:USERPROFILE\Downloads\*DynamicIsland*.exe" -ErrorAction SilentlyContinue | ForEach-Object {
        Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
    }
} catch { }

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$projPath = Join-Path $scriptDir "src\WindowsDynamicIsland\WindowsDynamicIsland.csproj"
$targetExePath = $null

# 2. Build from source if inside repo and .NET SDK is available, otherwise download prebuilt standalone Release EXE
if ((Test-Path $projPath) -and (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    $dotnetVer = dotnet --version
    Write-Host "[2/4] Found local repository and .NET SDK ($dotnetVer). Building Release binary..." -ForegroundColor Yellow
    dotnet build "$projPath" -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] Build failed! Check compiler output above." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    $binDir = Join-Path $scriptDir "src\WindowsDynamicIsland\bin\Release"
    $builtExe = Get-ChildItem -Path $binDir -Filter "WindowsDynamicIsland.exe" -Recurse | Select-Object -First 1
    if ($builtExe -and (Test-Path $builtExe.FullName)) {
        $targetExePath = $builtExe.FullName
        Write-Host "  -> Compiled binary: $targetExePath" -ForegroundColor Green
    }
}

if (-not $targetExePath) {
    Write-Host "[2/4] Downloading standalone WindowsDynamicIsland.exe from GitHub Releases..." -ForegroundColor Yellow
    $installDir = Join-Path $env:LOCALAPPDATA "WindowsDynamicIsland"
    if (-not (Test-Path $installDir)) {
        New-Item -ItemType Directory -Path $installDir -Force | Out-Null
    }

    $targetExePath = Join-Path $installDir "WindowsDynamicIsland.exe"
    $downloadUrl = "https://github.com/lepi-da-vinci/dynamic-island/releases/latest/download/WindowsDynamicIsland.exe"

    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $downloadUrl -OutFile $targetExePath -UseBasicParsing
    $ProgressPreference = 'Continue'

    Write-Host "  -> Installed standalone binary to: $targetExePath" -ForegroundColor Green
}

# 3. Remove Windows SmartScreen Mark-of-the-Web (Zone.Identifier)
Write-Host "[3/4] Removing SmartScreen block (Unblock-File) and creating Desktop shortcut..." -ForegroundColor Yellow
try {
    Unblock-File -Path $targetExePath -ErrorAction SilentlyContinue
} catch { }

$desktopPath = [Environment]::GetFolderPath("Desktop")
$shortcutPath = Join-Path $desktopPath "Windows Dynamic Island.lnk"
$exeDir = Split-Path -Parent $targetExePath

try {
    $wshShell = New-Object -ComObject WScript.Shell
    $shortcut = $wshShell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $targetExePath
    $shortcut.WorkingDirectory = $exeDir
    $shortcut.Description = "Windows Dynamic Island"
    $shortcut.Save()
    Write-Host "  -> Desktop shortcut created: $shortcutPath" -ForegroundColor Green
} catch {
    # Ignore shortcut creation failure
}

# 4. Launch Application
Write-Host "[4/4] Starting Windows Dynamic Island..." -ForegroundColor Yellow
Start-Process -FilePath $targetExePath -WorkingDirectory $exeDir

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  [SUCCESS] Windows Dynamic Island is now running at the top of your screen!" -ForegroundColor Green
Write-Host "  Tip: Hover mouse to expand, Right-Click to cycle tabs, Scroll to change volume." -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Green
