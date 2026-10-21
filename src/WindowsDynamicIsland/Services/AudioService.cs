using System.Runtime.InteropServices;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Zero-dependency Windows Core Audio service using native COM interfaces (WASAPI / EndpointVolume)
/// for real-time audio peak visualization and system master volume / mute control.
/// </summary>
public sealed class AudioService
{
    private IAudioEndpointVolume? _endpointVolume;
    private IAudioMeterInformation? _meterInfo;
    private double _phase;

    public AudioService()
    {
        InitializeCoreAudio();
    }

    private void InitializeCoreAudio()
    {
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType == null) return;

            var enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumeratorType);
            if (enumerator == null) return;

            // eRender = 0, eMultimedia = 1
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) == 0 && device != null)
            {
                Guid iidVolume = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iidVolume, 23, IntPtr.Zero, out var volObj) == 0)
                {
                    _endpointVolume = volObj as IAudioEndpointVolume;
                }

                Guid iidMeter = typeof(IAudioMeterInformation).GUID;
                if (device.Activate(ref iidMeter, 23, IntPtr.Zero, out var meterObj) == 0)
                {
                    _meterInfo = meterObj as IAudioMeterInformation;
                }
            }
        }
        catch
        {
            // Fallback gracefully if no audio output device is connected
        }
    }

    public float GetCurrentPeakLevel(bool isMediaPlaying)
    {
        try
        {
            if (_meterInfo != null && _meterInfo.GetPeakValue(out float peak) == 0)
            {
                if (peak > 0.005f)
                {
                    return Math.Clamp(peak, 0f, 1f);
                }
            }
        }
        catch
        {
            // Ignore transient device switch
        }

        // If GSMTC reports playing but peak is low/unavailable, synthesize a gentle organic pulse
        if (isMediaPlaying)
        {
            _phase += 0.14;
            return (float)(0.32 + 0.22 * Math.Sin(_phase));
        }

        return 0f;
    }

    /// <summary>
    /// Fills a 5-bar spectrum array with organic heights (4.0 to maxBarHeight) driven by live WASAPI audio peak.
    /// </summary>
    public void UpdateSpectrumBars(Span<double> barHeights, bool isMediaPlaying, double maxBarHeight = 18.0)
    {
        float peak = GetCurrentPeakLevel(isMediaPlaying);
        _phase += 0.22;

        if (peak < 0.01f && !isMediaPlaying)
        {
            for (int i = 0; i < barHeights.Length; i++)
            {
                barHeights[i] = 4.0;
            }
            return;
        }

        double energy = Math.Clamp(peak * 1.45, 0.18, 1.0);
        ReadOnlySpan<double> phaseOffsets = [0.0, 1.3, 2.5, 3.8, 5.1];
        ReadOnlySpan<double> bandMultipliers = [0.70, 0.95, 1.00, 0.88, 0.65];

        for (int i = 0; i < barHeights.Length; i++)
        {
            double wave = 0.52 + 0.48 * Math.Sin(_phase * (1.0 + i * 0.17) + phaseOffsets[i % 5]);
            double target = 4.0 + (maxBarHeight - 4.0) * energy * bandMultipliers[i % 5] * wave;
            // Smooth interpolation
            barHeights[i] = barHeights[i] + (target - barHeights[i]) * 0.38;
        }
    }

    public int GetMasterVolumePercent()
    {
        try
        {
            if (_endpointVolume != null && _endpointVolume.GetMasterVolumeLevelScalar(out float level) == 0)
            {
                return (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100);
            }
        }
        catch { }
        return 65;
    }

    public int AdjustMasterVolume(int deltaPercent)
    {
        try
        {
            if (_endpointVolume != null && _endpointVolume.GetMasterVolumeLevelScalar(out float current) == 0)
            {
                float next = Math.Clamp(current + (deltaPercent / 100f), 0f, 1f);
                Guid ctx = Guid.Empty;
                _endpointVolume.SetMasterVolumeLevelScalar(next, ref ctx);
                if (next > 0f)
                {
                    _endpointVolume.SetMute(false, ref ctx);
                }
                return (int)Math.Round(next * 100);
            }
        }
        catch { }
        return GetMasterVolumePercent();
    }

    public bool IsMuted()
    {
        try
        {
            if (_endpointVolume != null && _endpointVolume.GetMute(out bool muted) == 0)
            {
                return muted;
            }
        }
        catch { }
        return false;
    }

    public bool ToggleMute()
    {
        try
        {
            if (_endpointVolume != null && _endpointVolume.GetMute(out bool muted) == 0)
            {
                bool next = !muted;
                Guid ctx = Guid.Empty;
                _endpointVolume.SetMute(next, ref ctx);
                return next;
            }
        }
        catch { }
        return false;
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? ppEndpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }
}

