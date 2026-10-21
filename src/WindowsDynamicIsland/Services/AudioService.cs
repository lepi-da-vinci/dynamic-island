using System.Runtime.InteropServices;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Zero-dependency Windows Core Audio service using native COM interfaces (WASAPI / EndpointVolume)
/// for real-time audio peak visualization, speaker master volume/mute, and global microphone mute.
/// </summary>
public sealed class AudioService
{
    private IAudioEndpointVolume? _speakerVolume;
    private IAudioMeterInformation? _speakerMeter;
    private IAudioEndpointVolume? _micVolume;
    private double _phase;

    private bool _baselineCaptured;
    private int _lastPolledVolume = -1;
    private bool _lastPolledSpeakerMute;
    private bool _lastPolledMicMute;
    private DateTime _suppressExternalNotifyUntilUtc = DateTime.MinValue;

    public event Action<int, bool, bool>? SpeakerVolumeOrMuteChanged;
    public event Action<bool>? MicrophoneMuteChanged;

    public AudioService()
    {
        InitializeCoreAudio();
    }

    public void ReinitializeEndpoints()
    {
        InitializeCoreAudio();
        _baselineCaptured = false;
    }

    private void InitializeCoreAudio()
    {
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (enumeratorType == null) return;

            var enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumeratorType);
            if (enumerator == null) return;

            Guid iidVolume = typeof(IAudioEndpointVolume).GUID;
            Guid iidMeter = typeof(IAudioMeterInformation).GUID;

            // 1. Speaker Output Endpoint (eRender = 0, eMultimedia = 1)
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var renderDevice) == 0 && renderDevice != null)
            {
                if (renderDevice.Activate(ref iidVolume, 23, IntPtr.Zero, out var volObj) == 0)
                {
                    _speakerVolume = volObj as IAudioEndpointVolume;
                }

                if (renderDevice.Activate(ref iidMeter, 23, IntPtr.Zero, out var meterObj) == 0)
                {
                    _speakerMeter = meterObj as IAudioMeterInformation;
                }
            }

            // 2. Microphone Capture Endpoint (eCapture = 1, eCommunications = 2 / eMultimedia = 1)
            if (enumerator.GetDefaultAudioEndpoint(1, 2, out var captureDevice) == 0 && captureDevice != null)
            {
                if (captureDevice.Activate(ref iidVolume, 23, IntPtr.Zero, out var micVolObj) == 0)
                {
                    _micVolume = micVolObj as IAudioEndpointVolume;
                }
            }
        }
        catch
        {
            // Fallback gracefully if audio devices are unavailable
        }
    }

    /// <summary>
    /// Detects external volume / mute changes (e.g. physical keyboard Fn volume keys, rotary knob, or Windows taskbar).
    /// </summary>
    public void PollExternalChanges()
    {
        int currentVol = GetMasterVolumePercent();
        bool currentMute = IsMuted();
        bool currentMicMute = IsMicrophoneMuted();

        if (!_baselineCaptured)
        {
            _lastPolledVolume = currentVol;
            _lastPolledSpeakerMute = currentMute;
            _lastPolledMicMute = currentMicMute;
            _baselineCaptured = true;
            return;
        }

        bool suppressed = DateTime.UtcNow < _suppressExternalNotifyUntilUtc;

        if (currentMute != _lastPolledSpeakerMute)
        {
            _lastPolledSpeakerMute = currentMute;
            _lastPolledVolume = currentVol;
            if (!suppressed)
            {
                SpeakerVolumeOrMuteChanged?.Invoke(currentVol, currentMute, true);
            }
        }
        else if (Math.Abs(currentVol - _lastPolledVolume) >= 1)
        {
            _lastPolledVolume = currentVol;
            if (!suppressed)
            {
                SpeakerVolumeOrMuteChanged?.Invoke(currentVol, currentMute, false);
            }
        }

        if (currentMicMute != _lastPolledMicMute)
        {
            _lastPolledMicMute = currentMicMute;
            if (!suppressed)
            {
                MicrophoneMuteChanged?.Invoke(currentMicMute);
            }
        }
    }

    public float GetCurrentPeakLevel(bool isMediaPlaying)
    {
        try
        {
            if (_speakerMeter != null && _speakerMeter.GetPeakValue(out float peak) == 0)
            {
                if (peak > 0.005f)
                {
                    return Math.Clamp(peak, 0f, 1f);
                }
            }
        }
        catch { }

        if (isMediaPlaying && !IsMuted())
        {
            _phase += 0.14;
            return (float)(0.32 + 0.22 * Math.Sin(_phase));
        }

        return 0f;
    }

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
            barHeights[i] = barHeights[i] + (target - barHeights[i]) * 0.38;
        }
    }

    public int GetMasterVolumePercent()
    {
        try
        {
            if (_speakerVolume != null && _speakerVolume.GetMasterVolumeLevelScalar(out float level) == 0)
            {
                return (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100);
            }
        }
        catch { }
        return 65;
    }

    public int SetMasterVolumePercent(int targetPercent)
    {
        try
        {
            _suppressExternalNotifyUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
            float scalar = Math.Clamp(targetPercent / 100f, 0f, 1f);
            if (_speakerVolume != null)
            {
                Guid ctx = Guid.Empty;
                _speakerVolume.SetMasterVolumeLevelScalar(scalar, ref ctx);
                if (scalar > 0f)
                {
                    _speakerVolume.SetMute(false, ref ctx);
                    _lastPolledSpeakerMute = false;
                }
                _lastPolledVolume = (int)Math.Round(scalar * 100);
                return _lastPolledVolume;
            }
        }
        catch { }
        return GetMasterVolumePercent();
    }

    public int AdjustMasterVolume(int deltaPercent)
    {
        int current = GetMasterVolumePercent();
        return SetMasterVolumePercent(current + deltaPercent);
    }

    public bool IsMuted()
    {
        try
        {
            if (_speakerVolume != null && _speakerVolume.GetMute(out bool muted) == 0)
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
            _suppressExternalNotifyUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
            if (_speakerVolume != null && _speakerVolume.GetMute(out bool muted) == 0)
            {
                bool next = !muted;
                Guid ctx = Guid.Empty;
                _speakerVolume.SetMute(next, ref ctx);
                _lastPolledSpeakerMute = next;
                return next;
            }
        }
        catch { }
        return false;
    }

    public bool IsMicrophoneMuted()
    {
        try
        {
            if (_micVolume != null && _micVolume.GetMute(out bool muted) == 0)
            {
                return muted;
            }
        }
        catch { }
        return false;
    }

    public bool ToggleMicrophoneMute()
    {
        try
        {
            _suppressExternalNotifyUntilUtc = DateTime.UtcNow.AddMilliseconds(250);
            if (_micVolume != null && _micVolume.GetMute(out bool muted) == 0)
            {
                bool next = !muted;
                Guid ctx = Guid.Empty;
                _micVolume.SetMute(next, ref ctx);
                _lastPolledMicMute = next;
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
