# Windows Dynamic Island - ASUS Battery Hardware Control Task Setup
# Registers SYSTEM scheduled tasks so battery limit can be toggled with zero UAC prompts at runtime.

$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Elevating privileges to register SYSTEM tasks..." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb runas -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

$task80 = "WindowsDynamicIsland-Battery80"
$task100 = "WindowsDynamicIsland-Battery100"

$cmd80 = "try { (Get-WmiObject -Namespace root/wmi -Class AsusAtkWmi_WMNB).DEVS(0x00120057, 80) } catch { Invoke-CimMethod -InputObject (Get-CimInstance -Namespace root/wmi -ClassName AsusAtkWmi_WMNB) -MethodName DEVS -Arguments @{Device_ID=[uint32]0x00120057; Control_status=[uint32]80} }"
$cmd100 = "try { (Get-WmiObject -Namespace root/wmi -Class AsusAtkWmi_WMNB).DEVS(0x00120057, 100) } catch { Invoke-CimMethod -InputObject (Get-CimInstance -Namespace root/wmi -ClassName AsusAtkWmi_WMNB) -MethodName DEVS -Arguments @{Device_ID=[uint32]0x00120057; Control_status=[uint32]100} }"

$action80 = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command `"$cmd80`""
$action100 = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command `"$cmd100`""

$principal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\SYSTEM" -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName $task80 -Action $action80 -Principal $principal -Settings $settings -Force | Out-Null
Register-ScheduledTask -TaskName $task100 -Action $action100 -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Tasks registered successfully!" -ForegroundColor Green

# Immediately apply 100% limit to verify
Start-Process schtasks.exe -ArgumentList "/run /tn `"$task100`"" -Wait -NoNewWindow
Write-Host "Executed $task100!" -ForegroundColor Green
