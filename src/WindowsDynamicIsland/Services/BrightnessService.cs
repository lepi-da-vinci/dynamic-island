using System.Runtime.InteropServices;
using WindowsDynamicIsland.Core;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Ultra-fast native Win32 kernel brightness controller via \\.\LCD DeviceIoControl,
/// with automatic Dxva2.dll DDC/CI fallback for external desktop monitors.
/// </summary>
public sealed class BrightnessService
{
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private const uint IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS = 0x00230498;
    private const uint IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS = 0x0023049C;

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private int _lastKnownBrightness = -1;
    private int _lastPolledBrightness = -1;
    private DateTime _suppressExternalNotifyUntilUtc = DateTime.MinValue;
    private IntPtr _targetMonitor;
    private bool _ddcSupported = true;
    private int _ddcConsecutiveFailures;

    public event Action<int>? BrightnessChanged;

    public void SetTargetMonitor(IntPtr hMonitor)
    {
        if (_targetMonitor != hMonitor)
        {
            _targetMonitor = hMonitor;
            _ddcSupported = true;
            _ddcConsecutiveFailures = 0;
        }
    }

    public void PollExternalChanges()
    {
        int current = GetBrightnessPercent();
        if (_lastPolledBrightness < 0)
        {
            _lastPolledBrightness = current;
            return;
        }

        if (Math.Abs(current - _lastPolledBrightness) >= 1)
        {
            _lastPolledBrightness = current;
            if (DateTime.UtcNow >= _suppressExternalNotifyUntilUtc)
            {
                BrightnessChanged?.Invoke(current);
            }
        }
    }

    public int GetBrightnessPercent()
    {
        IntPtr hLcd = CreateFile(
            @"\\.\LCD",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (hLcd == IntPtr.Zero || hLcd == INVALID_HANDLE_VALUE)
        {
            if (TryGetDdcBrightness(out int ddcVal))
            {
                _lastKnownBrightness = ddcVal;
                return ddcVal;
            }
            return _lastKnownBrightness >= 0 ? _lastKnownBrightness : 50;
        }

        try
        {
            if (DeviceIoControlQuery(
                hLcd,
                IOCTL_VIDEO_QUERY_DISPLAY_BRIGHTNESS,
                IntPtr.Zero,
                0,
                out var brightness,
                (uint)Marshal.SizeOf<DISPLAY_BRIGHTNESS>(),
                out _,
                IntPtr.Zero))
            {
                int val = brightness.ucDisplayPolicy == 1
                    ? brightness.ucACBrightness
                    : brightness.ucDCBrightness > 0 ? brightness.ucDCBrightness : brightness.ucACBrightness;

                _lastKnownBrightness = Math.Clamp(val, 0, 100);
                return _lastKnownBrightness;
            }
        }
        catch
        {
            // Ignore hardware query error
        }
        finally
        {
            CloseHandle(hLcd);
        }

        if (TryGetDdcBrightness(out int ddcFallback))
        {
            _lastKnownBrightness = ddcFallback;
            return ddcFallback;
        }

        return _lastKnownBrightness >= 0 ? _lastKnownBrightness : 50;
    }

    public int SetBrightnessPercent(int targetPercent)
    {
        int clamped = Math.Clamp(targetPercent, 0, 100);
        _suppressExternalNotifyUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
        _lastPolledBrightness = clamped;

        IntPtr hLcd = CreateFile(
            @"\\.\LCD",
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (hLcd == IntPtr.Zero || hLcd == INVALID_HANDLE_VALUE)
        {
            TrySetDdcBrightness(clamped);
            _lastKnownBrightness = clamped;
            return clamped;
        }

        try
        {
            var payload = new DISPLAY_BRIGHTNESS
            {
                ucDisplayPolicy = 3, // DISPLAYPOLICY_BOTH (AC & DC)
                ucACBrightness = (byte)clamped,
                ucDCBrightness = (byte)clamped
            };

            DeviceIoControlSet(
                hLcd,
                IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS,
                ref payload,
                (uint)Marshal.SizeOf<DISPLAY_BRIGHTNESS>(),
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);

            _lastKnownBrightness = clamped;
        }
        catch
        {
            // Ignore set error
        }
        finally
        {
            CloseHandle(hLcd);
        }

        return _lastKnownBrightness;
    }

    public int AdjustBrightness(int deltaPercent)
    {
        int current = GetBrightnessPercent();
        return SetBrightnessPercent(current + deltaPercent);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAY_BRIGHTNESS
    {
        public byte ucDisplayPolicy;
        public byte ucACBrightness;
        public byte ucDCBrightness;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlQuery(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        out DISPLAY_BRIGHTNESS lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlSet(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref DISPLAY_BRIGHTNESS lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    private IntPtr GetActiveMonitorHandle()
    {
        if (_targetMonitor != IntPtr.Zero) return _targetMonitor;
        return Win32Interop.ResolveTargetMonitor("Primary")?.HMonitor ?? IntPtr.Zero;
    }

    private bool TryGetDdcBrightness(out int percent)
    {
        percent = 50;
        if (!_ddcSupported) return false;

        IntPtr hMonitor = GetActiveMonitorHandle();
        if (hMonitor == IntPtr.Zero) return false;

        PHYSICAL_MONITOR[]? physicalMonitors = null;
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
            {
                MarkDdcFailure();
                return false;
            }

            physicalMonitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicalMonitors))
            {
                MarkDdcFailure();
                return false;
            }

            if (GetMonitorBrightness(physicalMonitors[0].hPhysicalMonitor, out uint min, out uint curr, out uint max) && max > min)
            {
                _ddcConsecutiveFailures = 0;
                percent = Math.Clamp((int)Math.Round((curr - min) * 100.0 / (max - min)), 0, 100);
                return true;
            }

            MarkDdcFailure();
        }
        catch
        {
            MarkDdcFailure();
        }
        finally
        {
            if (physicalMonitors != null && physicalMonitors.Length > 0)
            {
                try { DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors); } catch { }
            }
        }

        return false;
    }

    private void TrySetDdcBrightness(int targetPercent)
    {
        if (!_ddcSupported) return;

        IntPtr hMonitor = GetActiveMonitorHandle();
        if (hMonitor == IntPtr.Zero) return;

        PHYSICAL_MONITOR[]? physicalMonitors = null;
        try
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count == 0)
            {
                MarkDdcFailure();
                return;
            }

            physicalMonitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, physicalMonitors))
            {
                MarkDdcFailure();
                return;
            }

            uint min = 0, max = 100;
            if (!GetMonitorBrightness(physicalMonitors[0].hPhysicalMonitor, out min, out _, out max) || max <= min)
            {
                min = 0;
                max = 100;
            }

            uint rawValue = (uint)Math.Clamp(Math.Round(min + (targetPercent / 100.0) * (max - min)), min, max);
            if (!SetMonitorBrightness(physicalMonitors[0].hPhysicalMonitor, rawValue))
            {
                MarkDdcFailure();
            }
            else
            {
                _ddcConsecutiveFailures = 0;
            }
        }
        catch
        {
            MarkDdcFailure();
        }
        finally
        {
            if (physicalMonitors != null && physicalMonitors.Length > 0)
            {
                try { DestroyPhysicalMonitors((uint)physicalMonitors.Length, physicalMonitors); } catch { }
            }
        }
    }

    private void MarkDdcFailure()
    {
        _ddcConsecutiveFailures++;
        if (_ddcConsecutiveFailures >= 3)
        {
            _ddcSupported = false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr hMonitor, out uint pdwMinimumBrightness, out uint pdwCurrentBrightness, out uint pdwMaximumBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint dwNewBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
