<#
.SYNOPSIS
    Windows Dynamic Island - Official Standard Installer Script
.DESCRIPTION
    Installs Windows Dynamic Island into %LOCALAPPDATA%\Programs\WindowsDynamicIsland,
    registers the application in Windows Settings > Installed Apps (Add or Remove Programs),
    creates Start Menu and Desktop shortcuts, configures Windows Startup, and launches the app.
#>

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Windows Dynamic Island - Official Windows Installer     " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Stop any running instances and unblock Downloads folder binaries
Write-Host "[1/5] Preparing environment and stopping running instances..." -ForegroundColor Yellow
Stop-Process -Name "WindowsDynamicIsland" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "WindowsDynamicIsland-Setup" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "Uninstall" -Force -ErrorAction SilentlyContinue

try {
    Get-ChildItem -Path "$env:USERPROFILE\Downloads\*DynamicIsland*.exe" -ErrorAction SilentlyContinue | ForEach-Object {
        Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
    }
} catch { }

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$projPath = Join-Path $scriptDir "src\WindowsDynamicIsland\WindowsDynamicIsland.csproj"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\WindowsDynamicIsland"
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

$installedExe = Join-Path $installDir "WindowsDynamicIsland.exe"
$uninstallerExe = Join-Path $installDir "Uninstall.exe"
$builtLocally = $false

# 2. Build from source if inside repo and .NET SDK is available, otherwise download prebuilt standalone Release EXE
if ((Test-Path $projPath) -and (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    $dotnetVer = dotnet --version
    Write-Host "[2/5] Found local repository and .NET SDK ($dotnetVer). Building Release binary..." -ForegroundColor Yellow
    dotnet build "$projPath" -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] Build failed! Check compiler output above." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    $binDir = Join-Path $scriptDir "src\WindowsDynamicIsland\bin\Release"
    $builtExe = Get-ChildItem -Path $binDir -Filter "WindowsDynamicIsland.exe" -Recurse | Select-Object -First 1
    if ($builtExe -and (Test-Path $builtExe.FullName)) {
        Get-ChildItem -Path $builtExe.DirectoryName -File | Where-Object { $_.Extension -ne ".pdb" } | ForEach-Object {
            Copy-Item -Path $_.FullName -Destination (Join-Path $installDir $_.Name) -Force
        }
        Copy-Item -Path $installedExe -Destination $uninstallerExe -Force
        $builtLocally = $true
        Write-Host "  -> Installed build files to: $installDir" -ForegroundColor Green
    }
}

if (-not $builtLocally) {
    Write-Host "[2/5] Downloading standalone WindowsDynamicIsland.exe from GitHub Releases..." -ForegroundColor Yellow
    $downloadUrl = "https://github.com/lepi-da-vinci/dynamic-island/releases/latest/download/WindowsDynamicIsland.exe"

    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $downloadUrl -OutFile $installedExe -UseBasicParsing
    $ProgressPreference = 'Continue'

    Copy-Item -Path $installedExe -Destination $uninstallerExe -Force
    Write-Host "  -> Installed standalone binary to: $installDir" -ForegroundColor Green
}

# 3. Remove Windows SmartScreen Mark-of-the-Web (Zone.Identifier)
Write-Host "[3/5] Removing SmartScreen block (Unblock-File)..." -ForegroundColor Yellow
try {
    Get-ChildItem -Path $installDir -File -ErrorAction SilentlyContinue | ForEach-Object {
        Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
    }
} catch { }

# 4. Register in Windows Installed Apps (Add/Remove Programs), Start Menu, Desktop & Startup
Write-Host "[4/5] Registering in Windows Installed Apps, Start Menu, and Desktop..." -ForegroundColor Yellow

$uninstallRegPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsDynamicIsland"
if (-not (Test-Path $uninstallRegPath)) {
    New-Item -Path $uninstallRegPath -Force | Out-Null
}

$totalBytes = (Get-ChildItem -Path $installDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$sizeKb = [int][Math]::Max(1024, ($totalBytes / 1024))

Set-ItemProperty -Path $uninstallRegPath -Name "DisplayName" -Value "Windows Dynamic Island" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "DisplayIcon" -Value "`"$installedExe`",0" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "DisplayVersion" -Value "1.1.0" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "Publisher" -Value "Lepi Da Vinci" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "InstallLocation" -Value $installDir -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "InstallDate" -Value (Get-Date -Format "yyyyMMdd") -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "UninstallString" -Value "`"$uninstallerExe`" --uninstall" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "QuietUninstallString" -Value "`"$uninstallerExe`" --uninstall --quiet" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "URLInfoAbout" -Value "https://github.com/lepi-da-vinci/dynamic-island" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallRegPath -Name "NoRepair" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallRegPath -Name "EstimatedSize" -Value $sizeKb -Type DWord

# Register Autostart
$runRegPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $runRegPath -Name "WindowsDynamicIsland" -Value "`"$installedExe`"" -Type String

# Create Desktop & Start Menu Shortcuts
try {
    $wshShell = New-Object -ComObject WScript.Shell

    $desktopPath = [Environment]::GetFolderPath("Desktop")
    $desktopShortcut = $wshShell.CreateShortcut((Join-Path $desktopPath "Windows Dynamic Island.lnk"))
    $desktopShortcut.TargetPath = $installedExe
    $desktopShortcut.WorkingDirectory = $installDir
    $desktopShortcut.Description = "Windows Dynamic Island"
    $desktopShortcut.IconLocation = "$installedExe,0"
    $desktopShortcut.Save()

    $programsDir = [Environment]::GetFolderPath("Programs")
    $startMenuFolder = Join-Path $programsDir "Windows Dynamic Island"
    if (-not (Test-Path $startMenuFolder)) {
        New-Item -ItemType Directory -Path $startMenuFolder -Force | Out-Null
    }

    $smRootShortcut = $wshShell.CreateShortcut((Join-Path $programsDir "Windows Dynamic Island.lnk"))
    $smRootShortcut.TargetPath = $installedExe
    $smRootShortcut.WorkingDirectory = $installDir
    $smRootShortcut.Description = "Windows Dynamic Island"
    $smRootShortcut.IconLocation = "$installedExe,0"
    $smRootShortcut.Save()

    $smAppShortcut = $wshShell.CreateShortcut((Join-Path $startMenuFolder "Windows Dynamic Island.lnk"))
    $smAppShortcut.TargetPath = $installedExe
    $smAppShortcut.WorkingDirectory = $installDir
    $smAppShortcut.Description = "Windows Dynamic Island"
    $smAppShortcut.IconLocation = "$installedExe,0"
    $smAppShortcut.Save()

    $smUninstallShortcut = $wshShell.CreateShortcut((Join-Path $startMenuFolder "Uninstall Windows Dynamic Island.lnk"))
    $smUninstallShortcut.TargetPath = $uninstallerExe
    $smUninstallShortcut.Arguments = "--uninstall"
    $smUninstallShortcut.WorkingDirectory = $installDir
    $smUninstallShortcut.Description = "Uninstall Windows Dynamic Island"
    $smUninstallShortcut.IconLocation = "$uninstallerExe,0"
    $smUninstallShortcut.Save()

    Write-Host "  -> Registered in Windows Installed Apps, Start Menu, Desktop, and Startup." -ForegroundColor Green
} catch {
    # Ignore shortcut creation failure
}

# 5. Launch Application
Write-Host "[5/5] Starting Windows Dynamic Island..." -ForegroundColor Yellow
Start-Process -FilePath $installedExe -WorkingDirectory $installDir

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  [SUCCESS] Windows Dynamic Island is now installed and running!" -ForegroundColor Green
Write-Host "  To Uninstall: Open Windows Settings -> Apps -> Installed Apps -> Windows Dynamic Island -> Uninstall" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Green
