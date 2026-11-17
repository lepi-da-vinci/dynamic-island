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
    public TimeSpan Duration { get; set; } = TimeSpan.Zero;
    public BitmapSource? Thumbnail { get; init; }
    public Color DominantColor { get; init; } = Color.FromRgb(56, 189, 248); // Sky blue default
    public Color SecondaryColor { get; init; } = Color.FromRgb(129, 140, 248); // Indigo accent default
}

public sealed class MediaSessionService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private DateTime _lastTimelineSyncUtc = DateTime.UtcNow;
    private TimeSpan _basePosition = TimeSpan.Zero;

    public MediaTrackInfo CurrentTrack { get; private set; } = new();

    public event Action<MediaTrackInfo>? TrackChanged;
    public event Action<MediaTrackInfo>? TrackDurationRefined;
    public event Action<MediaTrackInfo>? PlaybackStateChanged;

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                _sessionManager.SessionsChanged += OnSessionsChanged;
                AttachToSession(SelectBestActiveSession(_sessionManager));
            }
        }
        catch
        {
            // GSMTC may not be available in restricted environments
        }
    }

    private static GlobalSystemMediaTransportControlsSession? SelectBestActiveSession(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        try
        {
            var sessions = manager.GetSessions();
            if (sessions != null)
            {
                foreach (var s in sessions)
                {
                    if (s?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        return s;
                    }
                }
            }
        }
        catch { }

        try
        {
            return manager.GetCurrentSession();
        }
        catch
        {
            return null;
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        try
        {
            AttachToSession(SelectBestActiveSession(sender));
        }
        catch
        {
            // Ignore transient session detach errors
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        try
        {
            var best = SelectBestActiveSession(sender);
            if (!ReferenceEquals(best, _currentSession))
            {
                AttachToSession(best);
            }
        }
        catch { }
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

            if (!isPlaying && _sessionManager != null)
            {
                var best = SelectBestActiveSession(_sessionManager);
                if (best != null && !ReferenceEquals(best, _currentSession) &&
                    best.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                {
                    AttachToSession(best);
                    return;
                }
            }

            UpdateTimelineFromSession();

            if (string.IsNullOrWhiteSpace(CurrentTrack.Title) || CurrentTrack.Title == "Tidak Ada Media")
            {
                await RefreshAllMetadataAsync(notifyTrackChange: true);
                return;
            }

            // Catch silent YouTube/Browser tab title changes when playback starts/resumes
            var props = await _currentSession.TryGetMediaPropertiesAsync();
            if (props != null && !string.IsNullOrWhiteSpace(props.Title) &&
                !string.Equals(props.Title.Trim(), CurrentTrack.Title, StringComparison.Ordinal))
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
                DominantColor = CurrentTrack.DominantColor,
                SecondaryColor = CurrentTrack.SecondaryColor
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
            Color secondary = Color.FromRgb(129, 140, 248);

            if (props.Thumbnail != null)
            {
                (thumbnail, dominant, secondary) = await LoadThumbnailAndColorAsync(props.Thumbnail);
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
                DominantColor = dominant,
                SecondaryColor = secondary
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

                TimeSpan newEnd = timeline.EndTime;
                if (newEnd > TimeSpan.FromSeconds(5) &&
                    Math.Abs((newEnd - CurrentTrack.Duration).TotalSeconds) > 3.5)
                {
                    CurrentTrack.Duration = newEnd;
                    if (CurrentTrack.HasActiveSession && CurrentTrack.Title != "Tidak Ada Media")
                    {
                        TrackDurationRefined?.Invoke(CurrentTrack);
                    }
                }
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

    private static async Task<(BitmapSource? Bitmap, Color Dominant, Color Secondary)> LoadThumbnailAndColorAsync(IRandomAccessStreamReference streamRef)
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

            var (dominant, secondary) = ExtractVibrantColors(bitmap);
            return (bitmap, dominant, secondary);
        }
        catch
        {
            return (null, Color.FromRgb(56, 189, 248), Color.FromRgb(129, 140, 248));
        }
    }

    private static (Color Dominant, Color Secondary) ExtractVibrantColors(BitmapSource bitmap)
    {
        try
        {
            var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            int width = formatted.PixelWidth;
            int height = formatted.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            formatted.CopyPixels(pixels, stride, 0);

            long sumR1 = 0, sumG1 = 0, sumB1 = 0, w1 = 0;
            long sumR2 = 0, sumG2 = 0, sumB2 = 0, w2 = 0;

            for (int i = 0; i < pixels.Length; i += 16)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                int saturation = max - min;

                if (max > 45 && max < 245 && saturation > 25)
                {
                    int weight = (saturation * 2) + 10;
                    // Split pixels spatially/luminance-wise so primary & secondary ambient mesh colors stay distinct
                    if (i < pixels.Length / 2)
                    {
                        sumR1 += r * weight;
                        sumG1 += g * weight;
                        sumB1 += b * weight;
                        w1 += weight;
                    }
                    else
                    {
                        sumR2 += r * weight;
                        sumG2 += g * weight;
                        sumB2 += b * weight;
                        w2 += weight;
                    }
                }
            }

            if (w1 > 0 || w2 > 0)
            {
                long totalW = w1 + w2;
                Color c1 = w1 > 0
                    ? Color.FromRgb(
                        (byte)Math.Clamp((sumR1 / w1) + 25, 60, 255),
                        (byte)Math.Clamp((sumG1 / w1) + 25, 60, 255),
                        (byte)Math.Clamp((sumB1 / w1) + 25, 60, 255))
                    : Color.FromRgb(
                        (byte)Math.Clamp(((sumR1 + sumR2) / totalW) + 25, 60, 255),
                        (byte)Math.Clamp(((sumG1 + sumG2) / totalW) + 25, 60, 255),
                        (byte)Math.Clamp(((sumB1 + sumB2) / totalW) + 25, 60, 255));

                Color c2 = w2 > 0
                    ? Color.FromRgb(
                        (byte)Math.Clamp((sumR2 / w2) + 18, 48, 245),
                        (byte)Math.Clamp((sumG2 / w2) + 18, 48, 245),
                        (byte)Math.Clamp((sumB2 / w2) + 18, 48, 245))
                    : Color.FromRgb(
                        (byte)Math.Clamp(c1.R - 30, 48, 245),
                        (byte)Math.Clamp(c1.G + 20, 48, 245),
                        (byte)Math.Clamp(c1.B + 45, 48, 255));

                return (c1, c2);
            }
        }
        catch { }

        return (Color.FromRgb(129, 140, 248), Color.FromRgb(56, 189, 248));
    }

    public void Dispose()
    {
        AttachToSession(null);
    }
}

