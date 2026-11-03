# Windows Dynamic Island - ASUS Battery Hardware Control Task Setup
# Registers SYSTEM scheduled tasks so battery limit can be toggled with zero UAC prompts at runtime.

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Meminta izin Administrator untuk mendaftarkan tugas sistem..." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb runas -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$task80 = "WindowsDynamicIsland-Battery80"
$task100 = "WindowsDynamicIsland-Battery100"

$scriptBlockText = @'
param([int]$Limit)

# 1. ASUS ACPI WMI method call to Embedded Controller (EC)
try {
    Invoke-CimMethod -Namespace root/wmi -ClassName AsusAtkWmi_WMNB -MethodName DEVS -Arguments @{Device_ID=[uint32]0x00120057; Control_status=[uint32]$Limit} | Out-Null
} catch {
    try {
        (Get-WmiObject -Namespace root/wmi -Class AsusAtkWmi_WMNB).DEVS(0x00120057, [uint32]$Limit) | Out-Null
    } catch {}
}

# 2. Sync ASUS Customization.ini
try {
    $iniPath = "C:\ProgramData\ASUS\ASUS System Control Interface\ASUSOptimization\Customization.ini"
    if (Test-Path $iniPath) {
        $content = Get-Content $iniPath -Raw
        $content = $content -replace "(\[BatteryHealthCharging\][^\[]*?value=)\d+", "`${1}$Limit"
        Set-Content -Path $iniPath -Value $content -Encoding UTF8
    }
} catch {}

# 3. Notify ASUS background component & Win32 Events
try {
    $files = Get-ChildItem -Path "C:\Windows\System32\DriverStore\FileRepository" -Filter "AsusOptimizationStartupTask.exe" -Recurse -ErrorAction SilentlyContinue
    if ($files -and $files.Count -gt 0) {
        Start-Process $files[0].FullName -ArgumentList "-bicustomization $Limit" -WindowStyle Hidden -Wait
    }
} catch {}

try {
    $eventName = if ($Limit -eq 100) { "Global\ASUS ASLDRSrv Battery Health Charging Full Charge Event" } else { "Global\ASUS ASLDRSrv Battery Health Charging Deferred Event" }
    $ev = [System.Threading.EventWaitHandle]::OpenExisting($eventName)
    $ev.Set() | Out-Null
} catch {}
'@

# Save helper script to Windows directory so SYSTEM task can invoke it cleanly
$helperScript = "$env:windir\System32\WindowsDynamicIsland-SetBatteryLimit.ps1"
Set-Content -Path $helperScript -Value $scriptBlockText -Encoding UTF8

$action80 = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$helperScript`" -Limit 80"
$action100 = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$helperScript`" -Limit 100"

$principal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $task80 -Action $action80 -Principal $principal -Settings $settings -Force | Out-Null
Register-ScheduledTask -TaskName $task100 -Action $action100 -Principal $principal -Settings $settings -Force | Out-Null

# Grant Authenticated Users SDDL permissions so standard non-elevated user apps can trigger via schtasks /run with ZERO UAC
try {
    $scheduler = New-Object -ComObject "Schedule.Service"
    $scheduler.Connect()
    $rootFolder = $scheduler.GetFolder("\")
    foreach ($tName in @($task80, $task100)) {
        $task = $rootFolder.GetTask($tName)
        $sddl = $task.GetSecurityDescriptor(0xF)
        if ($sddl -notmatch "A;;(GRGX|0x1200a9);;;AU") {
            $task.SetSecurityDescriptor($sddl + "(A;;GRGX;;;AU)", 0)
        }
    }
} catch {
    Write-Warning "Peringatan izin COM SDDL: $_"
}

# Grant file-level permissions as fallback
$taskFile80 = "$env:windir\System32\Tasks\$task80"
$taskFile100 = "$env:windir\System32\Tasks\$task100"
if (Test-Path $taskFile80) {
    icacls $taskFile80 /grant "Users:(R,X)" /grant "Authenticated Users:(R,X)" /Q | Out-Null
}
if (Test-Path $taskFile100) {
    icacls $taskFile100 /grant "Users:(R,X)" /grant "Authenticated Users:(R,X)" /Q | Out-Null
}

Write-Host "Tugas sistem Windows Dynamic Island berhasil didaftarkan!" -ForegroundColor Green

# Immediately apply 100% full charge limit to hardware
Start-Process schtasks.exe -ArgumentList "/run /tn `"$task100`"" -Wait -NoNewWindow
Write-Host "Hardware Full Capacity 100% aktif pada motherboard!" -ForegroundColor Green
