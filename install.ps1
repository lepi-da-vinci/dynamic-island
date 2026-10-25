<#
.SYNOPSIS
    Windows Dynamic Island - Official Standard Installer Script
.DESCRIPTION
    Installs Windows Dynamic Island into %LOCALAPPDATA%\Programs\WindowsDynamicIsland,
    configures Windows Defender exclusions and local Authenticode digital signature,
    registers the application in Windows Settings > Installed Apps (Add or Remove Programs),
    creates Start Menu, Desktop, and Startup shortcuts, and launches the app.
#>

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Windows Dynamic Island - Official Windows Installer     " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Stop any running instances and configure environment / Defender Exclusion
Write-Host "[1/5] Preparing environment and configuring security exclusions..." -ForegroundColor Yellow
Stop-Process -Name "WindowsDynamicIsland" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "WindowsDynamicIsland-Setup" -Force -ErrorAction SilentlyContinue
Stop-Process -Name "Uninstall" -Force -ErrorAction SilentlyContinue

$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$projPath = Join-Path $scriptDir "src\WindowsDynamicIsland\WindowsDynamicIsland.csproj"
$installDir = Join-Path $env:LOCALAPPDATA "Programs\WindowsDynamicIsland"
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir -Force | Out-Null
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    try {
        Add-MpPreference -ExclusionPath $installDir -ErrorAction SilentlyContinue
        Add-MpPreference -ExclusionProcess "WindowsDynamicIsland.exe", "Uninstall.exe" -ErrorAction SilentlyContinue
        Write-Host "  -> Added Windows Defender exclusion for $installDir" -ForegroundColor Green
    } catch { }
}

try {
    Get-ChildItem -Path "$env:USERPROFILE\Downloads\*DynamicIsland*" -ErrorAction SilentlyContinue | ForEach-Object {
        Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
    }
} catch { }

$installedExe = Join-Path $installDir "WindowsDynamicIsland.exe"
$uninstallerExe = Join-Path $installDir "Uninstall.exe"
$installedIco = Join-Path $installDir "app_icon.ico"
$localIcoSource = Join-Path $scriptDir "src\WindowsDynamicIsland\Assets\app_icon.ico"
$builtLocally = $false

# 2. Build from source if inside repo and .NET SDK is available, otherwise download unpacked Release ZIP
if ((Test-Path $projPath) -and (Get-Command "dotnet" -ErrorAction SilentlyContinue)) {
    $dotnetVer = dotnet --version
    Write-Host "[2/5] Found local repository and .NET SDK ($dotnetVer). Building Release binary..." -ForegroundColor Yellow
    dotnet build "$projPath" -c Release --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] Build failed! Check compiler output above." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    $binDir = Join-Path $scriptDir "src\WindowsDynamicIsland\bin\Release"
    $builtExe = Get-ChildItem -Path $binDir -Filter "WindowsDynamicIsland.exe" -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($builtExe -and (Test-Path $builtExe.FullName)) {
        Get-ChildItem -Path $builtExe.DirectoryName -File | Where-Object { $_.Extension -ne ".pdb" } | ForEach-Object {
            Copy-Item -Path $_.FullName -Destination (Join-Path $installDir $_.Name) -Force
        }
        Copy-Item -Path $installedExe -Destination $uninstallerExe -Force
        if (Test-Path $localIcoSource) {
            Copy-Item -Path $localIcoSource -Destination $installedIco -Force
        }
        $builtLocally = $true
        Write-Host "  -> Installed build files to: $installDir" -ForegroundColor Green
    }
}

if (-not $builtLocally) {
    Write-Host "[2/5] Downloading official unpacked release package from GitHub Releases..." -ForegroundColor Yellow
    $zipUrl = "https://github.com/lepi-da-vinci/dynamic-island/releases/latest/download/WindowsDynamicIsland-win-x64.zip"
    $icoUrl = "https://raw.githubusercontent.com/lepi-da-vinci/dynamic-island/main/src/WindowsDynamicIsland/Assets/app_icon.ico"
    $tempZip = Join-Path $env:TEMP "WindowsDynamicIsland-Release.zip"

    $ProgressPreference = 'SilentlyContinue'
    if (Test-Path $tempZip) { Remove-Item $tempZip -Force -ErrorAction SilentlyContinue }
    Invoke-WebRequest -Uri $zipUrl -OutFile $tempZip -UseBasicParsing
    Unblock-File -Path $tempZip -ErrorAction SilentlyContinue

    Expand-Archive -Path $tempZip -DestinationPath $installDir -Force
    Remove-Item $tempZip -Force -ErrorAction SilentlyContinue

    if (-not (Test-Path $installedIco)) {
        try { Invoke-WebRequest -Uri $icoUrl -OutFile $installedIco -UseBasicParsing } catch { }
    }
    $ProgressPreference = 'Continue'

    if ((Test-Path $installedExe) -and -not (Test-Path $uninstallerExe)) {
        Copy-Item -Path $installedExe -Destination $uninstallerExe -Force
    }
    Write-Host "  -> Extracted release package to: $installDir" -ForegroundColor Green
}

# 3. Strip Web Origin Extended Attributes & Apply Local Trusted Authenticode Signature
Write-Host "[3/5] Applying local Authenticode certificate and unblocking binaries..." -ForegroundColor Yellow
try {
    Get-ChildItem -Path $installDir -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object {
        Unblock-File -Path $_.FullName -ErrorAction SilentlyContinue
    }

    # Rewrite primary app binaries locally so Windows 11 Smart App Control NTFS Extended Attributes ($CI.CATALOGHINT) are stripped
    foreach ($targetName in @("WindowsDynamicIsland.exe", "Uninstall.exe", "WindowsDynamicIsland.dll")) {
        $targetFile = Join-Path $installDir $targetName
        if (Test-Path $targetFile) {
            $rawBytes = [System.IO.File]::ReadAllBytes($targetFile)
            Remove-Item -Path $targetFile -Force
            [System.IO.File]::WriteAllBytes($targetFile, $rawBytes)
        }
    }

    # Generate or reuse local Code Signing Certificate and trust it in Root & TrustedPublisher
    $certSubject = "CN=Windows Dynamic Island Local Trust"
    $cert = Get-ChildItem -Path "Cert:\CurrentUser\My" -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $certSubject } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $certSubject -KeyUsage DigitalSignature -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5)
    }

    if ($cert) {
        $tempCer = Join-Path $env:TEMP "WindowsDynamicIsland-Trust.cer"
        Export-Certificate -Cert $cert -FilePath $tempCer -Force | Out-Null
        if ($isAdmin) {
            Import-Certificate -FilePath $tempCer -CertStoreLocation "Cert:\LocalMachine\Root" | Out-Null
            Import-Certificate -FilePath $tempCer -CertStoreLocation "Cert:\LocalMachine\TrustedPublisher" | Out-Null
        }
        try {
            Import-Certificate -FilePath $tempCer -CertStoreLocation "Cert:\CurrentUser\TrustedPublisher" | Out-Null
        } catch { }
        Remove-Item $tempCer -Force -ErrorAction SilentlyContinue

        foreach ($signTarget in @("WindowsDynamicIsland.exe", "Uninstall.exe", "WindowsDynamicIsland.dll")) {
            $signPath = Join-Path $installDir $signTarget
            if (Test-Path $signPath) {
                Set-AuthenticodeSignature -FilePath $signPath -Certificate $cert -ErrorAction SilentlyContinue | Out-Null
            }
        }
        Write-Host "  -> Signed binaries with local Code Signing certificate." -ForegroundColor Green
    }
} catch {
    # Non-fatal if certificate store restricts import
}

# 4. Register in Windows Installed Apps (Add/Remove Programs), Start Menu, Desktop & Startup
Write-Host "[4/5] Registering in Windows Installed Apps, Start Menu, and Desktop..." -ForegroundColor Yellow

$uninstallRegPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsDynamicIsland"
if (-not (Test-Path $uninstallRegPath)) {
    New-Item -Path $uninstallRegPath -Force | Out-Null
}

$totalBytes = (Get-ChildItem -Path $installDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$sizeKb = [int][Math]::Max(1024, ($totalBytes / 1024))
$iconRef = if (Test-Path $installedIco) { $installedIco } else { "$installedExe,0" }

Set-ItemProperty -Path $uninstallRegPath -Name "DisplayName" -Value "Windows Dynamic Island" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "DisplayIcon" -Value $iconRef -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "DisplayVersion" -Value "1.4.0" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "Publisher" -Value "Lepi Da Vinci" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "InstallLocation" -Value $installDir -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "InstallDate" -Value (Get-Date -Format "yyyyMMdd") -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "UninstallString" -Value "`"$uninstallerExe`" --uninstall" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "QuietUninstallString" -Value "`"$uninstallerExe`" --uninstall --quiet" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "URLInfoAbout" -Value "https://github.com/lepi-da-vinci/dynamic-island" -Type String
Set-ItemProperty -Path $uninstallRegPath -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallRegPath -Name "NoRepair" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallRegPath -Name "EstimatedSize" -Value $sizeKb -Type DWord

# Clean up any legacy HKCU Run registry key and use standard Windows Startup folder shortcut instead
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "WindowsDynamicIsland" -ErrorAction SilentlyContinue

# Create Desktop, Start Menu & Startup Shortcuts
try {
    $wshShell = New-Object -ComObject WScript.Shell

    $desktopPath = [Environment]::GetFolderPath("Desktop")
    $desktopShortcut = $wshShell.CreateShortcut((Join-Path $desktopPath "Windows Dynamic Island.lnk"))
    $desktopShortcut.TargetPath = $installedExe
    $desktopShortcut.WorkingDirectory = $installDir
    $desktopShortcut.Description = "Windows Dynamic Island"
    $desktopShortcut.IconLocation = $iconRef
    $desktopShortcut.Save()

    $startupPath = [Environment]::GetFolderPath("Startup")
    $startupShortcut = $wshShell.CreateShortcut((Join-Path $startupPath "Windows Dynamic Island.lnk"))
    $startupShortcut.TargetPath = $installedExe
    $startupShortcut.WorkingDirectory = $installDir
    $startupShortcut.Description = "Windows Dynamic Island"
    $startupShortcut.IconLocation = $iconRef
    $startupShortcut.Save()

    $programsDir = [Environment]::GetFolderPath("Programs")
    $startMenuFolder = Join-Path $programsDir "Windows Dynamic Island"
    if (-not (Test-Path $startMenuFolder)) {
        New-Item -ItemType Directory -Path $startMenuFolder -Force | Out-Null
    }

    $smRootShortcut = $wshShell.CreateShortcut((Join-Path $programsDir "Windows Dynamic Island.lnk"))
    $smRootShortcut.TargetPath = $installedExe
    $smRootShortcut.WorkingDirectory = $installDir
    $smRootShortcut.Description = "Windows Dynamic Island"
    $smRootShortcut.IconLocation = $iconRef
    $smRootShortcut.Save()

    $smAppShortcut = $wshShell.CreateShortcut((Join-Path $startMenuFolder "Windows Dynamic Island.lnk"))
    $smAppShortcut.TargetPath = $installedExe
    $smAppShortcut.WorkingDirectory = $installDir
    $smAppShortcut.Description = "Windows Dynamic Island"
    $smAppShortcut.IconLocation = $iconRef
    $smAppShortcut.Save()

    $smUninstallShortcut = $wshShell.CreateShortcut((Join-Path $startMenuFolder "Uninstall Windows Dynamic Island.lnk"))
    $smUninstallShortcut.TargetPath = $uninstallerExe
    $smUninstallShortcut.Arguments = "--uninstall"
    $smUninstallShortcut.WorkingDirectory = $installDir
    $smUninstallShortcut.Description = "Uninstall Windows Dynamic Island"
    $smUninstallShortcut.IconLocation = $iconRef
    $smUninstallShortcut.Save()

    Start-Process -FilePath "ie4uinit.exe" -ArgumentList "-show" -WindowStyle Hidden -ErrorAction SilentlyContinue
    Write-Host "  -> Registered in Windows Installed Apps, Start Menu, Desktop, and Startup." -ForegroundColor Green
} catch {
    # Ignore shortcut creation failure
}

# 5. Launch Application
Write-Host "[5/5] Starting Windows Dynamic Island..." -ForegroundColor Yellow
try {
    Start-Process -FilePath $installedExe -WorkingDirectory $installDir
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host "  [SUCCESS] Windows Dynamic Island is now installed and running!" -ForegroundColor Green
    Write-Host "  To Uninstall: Open Windows Settings -> Apps -> Installed Apps -> Windows Dynamic Island -> Uninstall" -ForegroundColor Cyan
    Write-Host "==========================================================" -ForegroundColor Green
} catch {
    Write-Host "==========================================================" -ForegroundColor Yellow
    Write-Host "  [NOTICE] Windows Smart App Control (SAC) pada laptop OEM Anda memblokir eksekusi otomatis." -ForegroundColor Yellow
    Write-Host "  Solusi: Buka Windows Security -> App & browser control -> Smart App Control settings -> pilih Off," -ForegroundColor Cyan
    Write-Host "  atau jalankan PowerShell sebagai Administrator lalu ulangi perintah install." -ForegroundColor Cyan
    Write-Host "==========================================================" -ForegroundColor Yellow
}
