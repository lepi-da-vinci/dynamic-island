using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WindowsDynamicIsland.Services;

/// <summary>
/// Zero-dependency Windows Core Audio service using native COM interfaces (WASAPI Loopback PCM Capture,
/// IAudioEndpointVolume, and IAudioMeterInformation) for real-time 5-band frequency spectrum analysis
/// synchronized to the actual song audio, dynamic TWS/earphone switching, speaker volume/mute, and mic mute.
/// </summary>
public sealed class AudioService
{
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _speakerVolume;
    private IAudioMeterInformation? _speakerMeter;
    private IAudioEndpointVolume? _micVolume;

    // Real-time WASAPI Loopback PCM capture for true song-synced frequency bars
    private IAudioClient? _loopbackClient;
    private IAudioCaptureClient? _captureClient;
    private int _loopbackChannels = 2;
    private int _loopbackSampleRate = 48000;
    private int _loopbackBitsPerSample = 32;

    // 5-band real-time frequency filter states & envelopes (Bass -> Treble)
    private readonly double[] _bandEnvelopes = new double[5];
    private double _lpState1;
    private double _lpState2;
    private double _lpState3;
    private double _prevSample;
    private double _rollingPeakMax = 0.12;
    private float _latestRealtimePeak;

    private string _activeSpeakerDeviceId = string.Empty;

    public string ActiveOutputDeviceName { get; private set; } = "Speaker";
    public bool IsHeadphoneOrTws { get; private set; }

    private bool _baselineCaptured;
    private int _lastPolledVolume = -1;
    private bool _lastPolledSpeakerMute;
    private bool _lastPolledMicMute;
    private DateTime _suppressExternalNotifyUntilUtc = DateTime.MinValue;

    public event Action<int, bool, bool>? SpeakerVolumeOrMuteChanged;
    public event Action<bool>? MicrophoneMuteChanged;
    public event Action<string, bool>? OutputDeviceChanged;

    public AudioService()
    {
        try
        {
            Windows.Media.Devices.MediaDevice.DefaultAudioRenderDeviceChanged += (_, _) => ReinitializeEndpoints();
        }
        catch { }

        InitializeCoreAudio();
    }

    public void ReinitializeEndpoints()
    {
        InitializeCoreAudio();
        _baselineCaptured = false;
        PollExternalChanges();
    }

    private void InitializeCoreAudio()
    {
        try
        {
            StopLoopbackCapture();

            if (_enumerator == null)
            {
                var enumeratorType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
                if (enumeratorType == null) return;
                _enumerator = (IMMDeviceEnumerator?)Activator.CreateInstance(enumeratorType);
            }

            if (_enumerator == null) return;

            Guid iidVolume = typeof(IAudioEndpointVolume).GUID;
            Guid iidMeter = typeof(IAudioMeterInformation).GUID;

            // 1. Speaker / Headphone / TWS Output Endpoint (eRender = 0, eMultimedia = 1)
            if (_enumerator.GetDefaultAudioEndpoint(0, 1, out var renderDevice) == 0 && renderDevice != null)
            {
                if (renderDevice.GetId(out string devId) == 0)
                {
                    _activeSpeakerDeviceId = devId ?? string.Empty;
                }

                if (renderDevice.Activate(ref iidVolume, 23, IntPtr.Zero, out var volObj) == 0)
                {
                    _speakerVolume = volObj as IAudioEndpointVolume;
                }

                if (renderDevice.Activate(ref iidMeter, 23, IntPtr.Zero, out var meterObj) == 0)
                {
                    _speakerMeter = meterObj as IAudioMeterInformation;
                }

                InitializeLoopbackCapture(renderDevice);
            }

            // 2. Microphone Capture Endpoint (eCapture = 1, eCommunications = 2)
            if (_enumerator.GetDefaultAudioEndpoint(1, 2, out var captureDevice) == 0 && captureDevice != null)
            {
                if (captureDevice.Activate(ref iidVolume, 23, IntPtr.Zero, out var micVolObj) == 0)
                {
                    _micVolume = micVolObj as IAudioEndpointVolume;
                }
            }

            RefreshDeviceNameAsync();
        }
        catch
        {
            // Fallback gracefully if audio devices are unavailable
        }
    }

    private void InitializeLoopbackCapture(IMMDevice renderDevice)
    {
        try
        {
            Guid iidClient = typeof(IAudioClient).GUID;
            if (renderDevice.Activate(ref iidClient, 23, IntPtr.Zero, out var clientObj) != 0 ||
                clientObj is not IAudioClient audioClient)
            {
                return;
            }

            if (audioClient.GetMixFormat(out IntPtr fmtPtr) != 0 || fmtPtr == IntPtr.Zero)
                return;

            _loopbackChannels = Math.Max(1, (int)Marshal.ReadInt16(fmtPtr, 2));
            _loopbackSampleRate = Math.Max(8000, Marshal.ReadInt32(fmtPtr, 4));
            _loopbackBitsPerSample = Marshal.ReadInt16(fmtPtr, 14);

            // Initialize 50ms shared loopback buffer (500,000 hns)
            int hrInit = audioClient.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK,
                500_000,
                0,
                fmtPtr,
                IntPtr.Zero);

            Marshal.FreeCoTaskMem(fmtPtr);

            if (hrInit != 0)
                return;

            Guid iidCapture = typeof(IAudioCaptureClient).GUID;
            if (audioClient.GetService(ref iidCapture, out var capObj) == 0 &&
                capObj is IAudioCaptureClient captureClient)
            {
                if (audioClient.Start() == 0)
                {
                    _loopbackClient = audioClient;
                    _captureClient = captureClient;
                }
            }
        }
        catch
        {
            StopLoopbackCapture();
        }
    }

    private void StopLoopbackCapture()
    {
        try
        {
            _loopbackClient?.Stop();
        }
        catch { }
        _captureClient = null;
        _loopbackClient = null;
    }

    private async void RefreshDeviceNameAsync()
    {
        try
        {
            string id = Windows.Media.Devices.MediaDevice.GetDefaultAudioRenderId(Windows.Media.Devices.AudioDeviceRole.Default);
            if (!string.IsNullOrWhiteSpace(id))
            {
                var info = await Windows.Devices.Enumeration.DeviceInformation.CreateFromIdAsync(id);
                if (info != null && !string.IsNullOrWhiteSpace(info.Name))
                {
                    string rawName = info.Name.Trim();
                    IsHeadphoneOrTws = CheckIfHeadphoneOrTws(rawName);
                    ActiveOutputDeviceName = CleanDeviceDisplayName(rawName, IsHeadphoneOrTws);
                    OutputDeviceChanged?.Invoke(ActiveOutputDeviceName, IsHeadphoneOrTws);
                    return;
                }
            }
        }
        catch { }

        IsHeadphoneOrTws = CheckIfHeadphoneOrTws(_activeSpeakerDeviceId);
        ActiveOutputDeviceName = IsHeadphoneOrTws ? "Earphone / TWS" : "Speaker";
        OutputDeviceChanged?.Invoke(ActiveOutputDeviceName, IsHeadphoneOrTws);
    }

    /// <summary>
    /// Extracts a clean, concise hardware name (e.g. "Headphones (EDIFIER X2 Plus)" -> "EDIFIER X2 Plus").
    /// </summary>
    private static string CleanDeviceDisplayName(string rawName, bool isHeadphone)
    {
        var match = Regex.Match(rawName, @"\(([^()]+)\)\s*$");
        if (match.Success)
        {
            string inner = match.Groups[1].Value.Trim();
            if (!inner.Contains("Realtek", StringComparison.OrdinalIgnoreCase) &&
                !inner.Contains("High Definition", StringComparison.OrdinalIgnoreCase) &&
                !inner.Contains("Audio Device", StringComparison.OrdinalIgnoreCase) &&
                inner.Length >= 3)
            {
                return inner.Length > 20 ? inner[..20].Trim() : inner;
            }
        }

        if (rawName.StartsWith("Speakers", StringComparison.OrdinalIgnoreCase) ||
            rawName.StartsWith("Speaker", StringComparison.OrdinalIgnoreCase))
        {
            return "Speaker";
        }

        if (rawName.StartsWith("Headphones", StringComparison.OrdinalIgnoreCase))
        {
            return isHeadphone ? "Headphones" : "Speaker";
        }

        return rawName.Length > 20 ? rawName[..20].Trim() : rawName;
    }

    private static bool CheckIfHeadphoneOrTws(string str)
    {
        if (string.IsNullOrWhiteSpace(str)) return false;
        return str.Contains("buds", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("airpod", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("edifier", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("sony", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("jbl", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("anker", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("soundcore", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("sennheiser", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("bose", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("headphone", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("earphone", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("headset", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("tws", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("wireless", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("bluetooth", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("wh-", StringComparison.OrdinalIgnoreCase) ||
               str.Contains("wf-", StringComparison.OrdinalIgnoreCase);
    }

    public void PollExternalChanges()
    {
        if (_enumerator != null && _enumerator.GetDefaultAudioEndpoint(0, 1, out var checkDev) == 0 && checkDev != null)
        {
            if (checkDev.GetId(out string curId) == 0 && !string.Equals(curId, _activeSpeakerDeviceId, StringComparison.OrdinalIgnoreCase))
            {
                ReinitializeEndpoints();
                return;
            }
        }

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

    /// <summary>
    /// Drains all available WASAPI loopback PCM packets and decomposes them into 5 real frequency bands
    /// (Sub-Bass, Low-Mid, Vocal Mid, Upper-Mid/Snare, Treble/Hi-Hat) so the equalizer bars match the exact song!
    /// </summary>
    private bool ProcessLoopbackPcmPackets()
    {
        if (_captureClient == null || _loopbackBitsPerSample != 32)
            return false;

        bool readAnyPacket = false;
        double sumBand0 = 0, sumBand1 = 0, sumBand2 = 0, sumBand3 = 0, sumBand4 = 0;
        int totalSamplesProcessed = 0;

        try
        {
            // Cutoffs scaled to sample rate (~120Hz, ~420Hz, ~1800Hz)
            double alpha1 = Math.Clamp(2.0 * Math.PI * 130.0 / _loopbackSampleRate, 0.01, 0.45);
            double alpha2 = Math.Clamp(2.0 * Math.PI * 480.0 / _loopbackSampleRate, 0.03, 0.65);
            double alpha3 = Math.Clamp(2.0 * Math.PI * 2100.0 / _loopbackSampleRate, 0.08, 0.88);

            while (_captureClient.GetNextPacketSize(out uint packetFrames) == 0 && packetFrames > 0)
            {
                if (_captureClient.GetBuffer(
                        out IntPtr dataPtr,
                        out uint numFrames,
                        out uint flags,
                        out _,
                        out _) != 0)
                {
                    break;
                }

                readAnyPacket = true;
                bool isSilent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || dataPtr == IntPtr.Zero;

                if (!isSilent && numFrames > 0)
                {
                    int channels = _loopbackChannels;
                    int step = Math.Max(1, (int)numFrames / 128); // Subsample up to 128 frames per packet for <0.02ms CPU

                    unsafe
                    {
                        float* samples = (float*)dataPtr.ToPointer();
                        for (int f = 0; f < (int)numFrames; f += step)
                        {
                            int idx = f * channels;
                            double mono = samples[idx];
                            if (channels >= 2)
                            {
                                mono = 0.5 * (samples[idx] + samples[idx + 1]);
                            }

                            // Cascaded 1-pole IIR filter bank splitting into 5 musical frequency bands
                            _lpState1 += alpha1 * (mono - _lpState1);
                            _lpState2 += alpha2 * (mono - _lpState2);
                            _lpState3 += alpha3 * (mono - _lpState3);

                            double subBass = _lpState1;                       // Band 0: < 130 Hz (Kick & Sub-Bass)
                            double bassMid = _lpState2 - _lpState1;           // Band 1: 130 - 480 Hz (Bassline & Warmth)
                            double vocalMid = _lpState3 - _lpState2;          // Band 2: 480 - 2100 Hz (Lead Vocals & Piano)
                            double upperMid = mono - _lpState3;               // Band 3: > 2100 Hz (Snare & Presence)
                            double trebleDiff = mono - _prevSample;           // Band 4: High-frequency transient / Hi-Hat
                            _prevSample = mono;

                            sumBand0 += subBass * subBass;
                            sumBand1 += bassMid * bassMid;
                            sumBand2 += vocalMid * vocalMid;
                            sumBand3 += upperMid * upperMid;
                            sumBand4 += trebleDiff * trebleDiff;
                            totalSamplesProcessed++;
                        }
                    }
                }

                _captureClient.ReleaseBuffer(numFrames);
            }

            if (totalSamplesProcessed > 0)
            {
                // Convert RMS energy per band with musical weighting
                Span<double> instantBands =
                [
                    Math.Sqrt(sumBand0 / totalSamplesProcessed) * 2.1,
                    Math.Sqrt(sumBand1 / totalSamplesProcessed) * 2.5,
                    Math.Sqrt(sumBand2 / totalSamplesProcessed) * 2.8,
                    Math.Sqrt(sumBand3 / totalSamplesProcessed) * 3.1,
                    Math.Sqrt(sumBand4 / totalSamplesProcessed) * 2.6
                ];

                double frameMax = 0.01;
                for (int i = 0; i < 5; i++)
                {
                    if (instantBands[i] > frameMax) frameMax = instantBands[i];
                }

                // Dynamic AGC (Automatic Gain Control) so both quiet & loud mastered tracks show full dynamic range
                if (frameMax > _rollingPeakMax)
                {
                    _rollingPeakMax = _rollingPeakMax + (frameMax - _rollingPeakMax) * 0.45;
                }
                else
                {
                    _rollingPeakMax = Math.Max(0.06, _rollingPeakMax * 0.985);
                }

                double maxEnv = 0;
                for (int i = 0; i < 5; i++)
                {
                    double normalized = Math.Clamp(instantBands[i] / _rollingPeakMax, 0.0, 1.0);
                    // Power curve (1.45) expands beat punch and drops near-silent gaps down to zero
                    double shaped = Math.Pow(normalized, 1.45);

                    double rate = shaped > _bandEnvelopes[i] ? 0.62 : 0.22; // Fast attack on beats, smooth decay
                    _bandEnvelopes[i] += (shaped - _bandEnvelopes[i]) * rate;

                    if (_bandEnvelopes[i] > maxEnv) maxEnv = _bandEnvelopes[i];
                }

                _latestRealtimePeak = (float)maxEnv;
                return true;
            }
            else if (readAnyPacket)
            {
                // Silent packets
                for (int i = 0; i < 5; i++)
                {
                    _bandEnvelopes[i] *= 0.75;
                }
                _latestRealtimePeak *= 0.75f;
                return true;
            }
        }
        catch
        {
            // Ignore transient loopback errors
        }

        return false;
    }

    public float GetCurrentPeakLevel(bool isMediaPlaying)
    {
        if (!isMediaPlaying || IsMuted())
            return 0f;

        return _latestRealtimePeak;
    }

    public void UpdateSpectrumBars(Span<double> barHeights, bool isMediaPlaying, double maxBarHeight = 18.0)
    {
        const double minBarHeight = 3.5;

        if (!isMediaPlaying || IsMuted())
        {
            for (int i = 0; i < barHeights.Length; i++)
            {
                barHeights[i] += (minBarHeight - barHeights[i]) * 0.35;
            }
            _latestRealtimePeak = 0f;
            return;
        }

        bool hasLoopback = ProcessLoopbackPcmPackets();

        if (!hasLoopback && _speakerMeter != null && _speakerMeter.GetPeakValue(out float rawMeterPeak) == 0)
        {
            // Fallback using dynamic contrast curve on hardware peak meter if loopback unavailable
            double shaped = Math.Pow(Math.Clamp(rawMeterPeak, 0f, 1f), 2.2);
            _latestRealtimePeak = (float)shaped;
            for (int i = 0; i < 5; i++)
            {
                _bandEnvelopes[i] += (shaped - _bandEnvelopes[i]) * 0.4;
            }
        }

        for (int i = 0; i < barHeights.Length; i++)
        {
            double bandLevel = _bandEnvelopes[i % 5];
            double target = minBarHeight + (maxBarHeight - minBarHeight) * Math.Clamp(bandLevel, 0.0, 1.0);
            barHeights[i] = target;
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
        [PreserveSig]
        int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig]
        int GetState(out int pdwState);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int ShareMode, uint StreamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr AudioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(int ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr ppData, out uint pNumFramesToRead, out uint pdwFlags, out ulong pu64DevicePosition, out ulong pu64QPCPosition);
        [PreserveSig] int ReleaseBuffer(uint NumFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint pNumFramesInNextPacket);
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
