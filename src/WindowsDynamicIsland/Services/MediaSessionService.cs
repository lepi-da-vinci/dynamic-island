using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WindowsDynamicIsland.Services;

public sealed class MediaTrackInfo
{
    public string Title { get; init; } = "Tidak Ada Media";
    public string Artist { get; init; } = "Putar lagu di Spotify atau Browser";
    public string Album { get; init; } = string.Empty;
    public bool IsPlaying { get; init; }
    public bool HasActiveSession { get; init; }
    public TimeSpan Position { get; set; } = TimeSpan.Zero;
    public TimeSpan Duration { get; init; } = TimeSpan.Zero;
    public BitmapSource? Thumbnail { get; init; }
    public Color DominantColor { get; init; } = Color.FromRgb(56, 189, 248); // Sky blue default
}

public sealed class MediaSessionService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private DateTime _lastTimelineSyncUtc = DateTime.UtcNow;
    private TimeSpan _basePosition = TimeSpan.Zero;

    public MediaTrackInfo CurrentTrack { get; private set; } = new();

    public event Action<MediaTrackInfo>? TrackChanged;
    public event Action<MediaTrackInfo>? PlaybackStateChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                AttachToSession(_sessionManager.GetCurrentSession());
            }
        }
        catch
        {
            // GSMTC may not be available in restricted environments
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        try
        {
            AttachToSession(sender.GetCurrentSession());
        }
        catch
        {
            // Ignore transient session detach errors
        }
    }

    private void AttachToSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_currentSession != null)
        {
            try
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }
            catch
            {
                // Ignore unsubscribe error
            }
        }

        _currentSession = session;

        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            _ = RefreshAllMetadataAsync(notifyTrackChange: true);
        }
        else
        {
            CurrentTrack = new MediaTrackInfo();
            TrackChanged?.Invoke(CurrentTrack);
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshAllMetadataAsync(notifyTrackChange: true);
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        _ = RefreshPlaybackStatusOnlyAsync();
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        UpdateTimelineFromSession();
    }

    private async Task RefreshPlaybackStatusOnlyAsync()
    {
        if (_currentSession == null) return;
        try
        {
            var playback = _currentSession.GetPlaybackInfo();
            bool isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            UpdateTimelineFromSession();

            if (string.IsNullOrWhiteSpace(CurrentTrack.Title) || CurrentTrack.Title == "Tidak Ada Media")
            {
                await RefreshAllMetadataAsync(notifyTrackChange: true);
                return;
            }

            CurrentTrack = new MediaTrackInfo
            {
                Title = CurrentTrack.Title,
                Artist = CurrentTrack.Artist,
                Album = CurrentTrack.Album,
                IsPlaying = isPlaying,
                HasActiveSession = true,
                Position = GetInterpolatedPosition(),
                Duration = CurrentTrack.Duration,
                Thumbnail = CurrentTrack.Thumbnail,
                DominantColor = CurrentTrack.DominantColor
            };

            PlaybackStateChanged?.Invoke(CurrentTrack);
        }
        catch
        {
            // Ignore transient WinRT errors
        }
    }

    public async Task RefreshAllMetadataAsync(bool notifyTrackChange = false)
    {
        var session = _currentSession;
        if (session == null) return;

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props == null) return;

            var playback = session.GetPlaybackInfo();
            bool isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var timeline = session.GetTimelineProperties();
            TimeSpan duration = timeline?.EndTime ?? TimeSpan.Zero;
            _basePosition = timeline?.Position ?? TimeSpan.Zero;
            _lastTimelineSyncUtc = DateTime.UtcNow;

            BitmapSource? thumbnail = null;
            Color dominant = Color.FromRgb(56, 189, 248);

            if (props.Thumbnail != null)
            {
                (thumbnail, dominant) = await LoadThumbnailAndColorAsync(props.Thumbnail);
            }

            string title = string.IsNullOrWhiteSpace(props.Title) ? "Media Aktif" : props.Title.Trim();
            string artist = string.IsNullOrWhiteSpace(props.Artist) ? "Aplikasi Desktop / Browser" : props.Artist.Trim();
            bool trackDifferent = title != CurrentTrack.Title || artist != CurrentTrack.Artist;

            CurrentTrack = new MediaTrackInfo
            {
                Title = title,
                Artist = artist,
                Album = props.AlbumTitle?.Trim() ?? string.Empty,
                IsPlaying = isPlaying,
                HasActiveSession = true,
                Position = _basePosition,
                Duration = duration,
                Thumbnail = thumbnail,
                DominantColor = dominant
            };

            if (notifyTrackChange || trackDifferent)
            {
                TrackChanged?.Invoke(CurrentTrack);
            }
            else
            {
                PlaybackStateChanged?.Invoke(CurrentTrack);
            }
        }
        catch
        {
            // Ignore transient GSMTC exceptions
        }
    }

    private void UpdateTimelineFromSession()
    {
        if (_currentSession == null) return;
        try
        {
            var timeline = _currentSession.GetTimelineProperties();
            if (timeline != null)
            {
                _basePosition = timeline.Position;
                _lastTimelineSyncUtc = DateTime.UtcNow;
                CurrentTrack.Position = _basePosition;
            }
        }
        catch
        {
            // Ignore
        }
    }

    public TimeSpan GetInterpolatedPosition()
    {
        if (!CurrentTrack.HasActiveSession) return TimeSpan.Zero;
        if (!CurrentTrack.IsPlaying) return _basePosition;

        TimeSpan elapsed = DateTime.UtcNow - _lastTimelineSyncUtc;
        TimeSpan estimated = _basePosition + elapsed;
        if (CurrentTrack.Duration > TimeSpan.Zero && estimated > CurrentTrack.Duration)
        {
            return CurrentTrack.Duration;
        }
        return estimated;
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession == null) return;
        try
        {
            await _currentSession.TryTogglePlayPauseAsync();
        }
        catch { }
    }

    public async Task SkipNextAsync()
    {
        if (_currentSession == null) return;
        try
        {
            await _currentSession.TrySkipNextAsync();
        }
        catch { }
    }

    public async Task SkipPreviousAsync()
    {
        if (_currentSession == null) return;
        try
        {
            await _currentSession.TrySkipPreviousAsync();
        }
        catch { }
    }

    public async Task SeekToPositionAsync(TimeSpan targetPosition)
    {
        if (_currentSession == null) return;
        try
        {
            if (targetPosition < TimeSpan.Zero)
                targetPosition = TimeSpan.Zero;
            if (CurrentTrack.Duration > TimeSpan.Zero && targetPosition > CurrentTrack.Duration)
                targetPosition = CurrentTrack.Duration;

            // Update local interpolated position immediately for responsive UI scrubbing
            _basePosition = targetPosition;
            _lastTimelineSyncUtc = DateTime.UtcNow;
            CurrentTrack.Position = targetPosition;

            await _currentSession.TryChangePlaybackPositionAsync(targetPosition.Ticks);
        }
        catch
        {
            // Some media players may not support seeking via GSMTC
        }
    }

    private static async Task<(BitmapSource? Bitmap, Color Dominant)> LoadThumbnailAndColorAsync(IRandomAccessStreamReference streamRef)
    {
        try
        {
            using IRandomAccessStreamWithContentType winrtStream = await streamRef.OpenReadAsync();
            using var netStream = winrtStream.AsStreamForRead();
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            ms.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 128;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();

            Color dominant = ExtractVibrantAccentColor(bitmap);
            return (bitmap, dominant);
        }
        catch
        {
            return (null, Color.FromRgb(56, 189, 248));
        }
    }

    private static Color ExtractVibrantAccentColor(BitmapSource bitmap)
    {
        try
        {
            var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int width = formatted.PixelWidth;
            int height = formatted.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            formatted.CopyPixels(pixels, stride, 0);

            long sumR = 0, sumG = 0, sumB = 0, weightSum = 0;

            for (int i = 0; i < pixels.Length; i += 16) // Sample every 4th pixel
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                int saturation = max - min;

                // Prefer saturated, mid-to-bright pixels for a vibrant Dynamic Island glow
                if (max > 45 && max < 245 && saturation > 25)
                {
                    int weight = saturation * 2 + 10;
                    sumR += r * weight;
                    sumG += g * weight;
                    sumB += b * weight;
                    weightSum += weight;
                }
            }

            if (weightSum > 0)
            {
                byte r = (byte)Math.Clamp(sumR / weightSum + 25, 60, 255);
                byte g = (byte)Math.Clamp(sumG / weightSum + 25, 60, 255);
                byte b = (byte)Math.Clamp(sumB / weightSum + 25, 60, 255);
                return Color.FromRgb(r, g, b);
            }
        }
        catch
        {
            // Fallback on default
        }

        return Color.FromRgb(129, 140, 248); // Indigo accent fallback
    }

    public void Dispose()
    {
        AttachToSession(null);
    }
}

