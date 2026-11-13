using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Services;

namespace WindowsDynamicIsland.UI;

/// <summary>
/// SMTC media session sync, synced lyrics UI, timeline scrubbing, per-app volume mixer, and audio output device switcher.
/// </summary>
public partial class MainWindow
{
    private void OnMediaTrackChanged(MediaTrackInfo track)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            _lastAnimatedLyricIndex = -999;
            bool hasSession = track.HasActiveSession && track.Title != "Tidak Ada Media";
            _stateMachine.SetMediaActive(hasSession);

            ApplyDominantColor(track.DominantColor);
            UpdateMediaAndLyricsUi();

            if (hasSession && _settingsService.MediaTrackChangeToastEnabled && (_stateMachine.IsTuckedMode || _stateMachine.CurrentState == IslandVisualState.TuckedTopEdge))
            {
                TriggerToast(new ToastPayload(
                    Icon: "MUSIC",
                    Title: track.Title,
                    Subtitle: string.IsNullOrWhiteSpace(track.Artist) ? "Sedang Memutar" : track.Artist,
                    AccentHex: $"#{track.DominantColor.R:X2}{track.DominantColor.G:X2}{track.DominantColor.B:X2}",
                    Duration: TimeSpan.FromSeconds(_settingsService.ToastDurationSeconds)));
            }

            if (hasSession)
            {
                await _lyricsService.LoadLyricsForTrackAsync(
                    track.Title,
                    track.Artist,
                    track.Album,
                    track.Duration.TotalSeconds);
            }
        });
    }

    private void OnMediaTrackDurationRefined(MediaTrackInfo track)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (track.HasActiveSession && track.Title != "Tidak Ada Media")
            {
                await _lyricsService.LoadLyricsForTrackAsync(
                    track.Title,
                    track.Artist,
                    track.Album,
                    track.Duration.TotalSeconds);
            }
        });
    }

    private void OnMediaPlaybackStateChanged(MediaTrackInfo track)
    {
        Dispatcher.InvokeAsync(() =>
        {
            bool hasSession = track.HasActiveSession && track.Title != "Tidak Ada Media";
            _stateMachine.SetMediaActive(hasSession);

            if (hasSession)
            {
                _autoTuckTimer.Stop();
            }
            else
            {
                if (_settingsService.AutoTuckEnabled && !_stateMachine.IsTuckedMode)
                {
                    _stateMachine.SetAutoTucked(false);
                    _autoTuckTimer.Stop();
                    _autoTuckTimer.Interval = TimeSpan.FromSeconds(10);
                    _autoTuckTimer.Start();
                }
            }

            UpdateMediaAndLyricsUi();
            EnsureRenderLoopRunning();
        });
    }

    private void ApplyDominantColor(Color c)
    {
        bool isMonochrome = _settingsService.IslandSurfaceTheme == "Monochrome" ||
                            _settingsService.AccentTheme == "Monochrome";
        Color effectiveMediaAccent = isMonochrome ? Color.FromRgb(226, 232, 240) : c;

        if (_settingsService.AccentTheme == "Auto")
        {
            AmbientGlowBrush.Color = effectiveMediaAccent;
            TuckedAccentBar.Background = BrushCache.Get(effectiveMediaAccent);
            if (_settingsService.RimGlowStyle == "AccentNeon")
            {
                UnifiedNotchRimPath.Stroke = BrushCache.Get(
                    Color.FromArgb(145, effectiveMediaAccent.R, effectiveMediaAccent.G, effectiveMediaAccent.B));
            }
        }
        AlbumShadowEffect.Color = isMonochrome ? Color.FromRgb(0, 0, 0) : c;

        var barBrush = BrushCache.Get(effectiveMediaAccent);
        TimelineFillBorder.Background = barBrush;
        LyricLineProgressFill.Background = barBrush;

        foreach (var rect in _waveRectangles)
        {
            rect.Fill = barBrush;
        }

        foreach (var rect in _expWaveRectangles)
        {
            rect.Fill = barBrush;
        }
    }

    private void UpdateMediaAndLyricsUi()
    {
        var track = _mediaService.CurrentTrack;

        CompactAlbumImage.Source = track.Thumbnail;
        ExpandedAlbumImage.Source = track.Thumbnail;
        ExpandedSongTitleText.Text = track.Title;
        ExpandedArtistText.Text = track.Artist;

        PlayPauseIconPath.Data = track.IsPlaying
            ? GetVectorGeometry("GeomPause")
            : GetVectorGeometry("GeomPlay");

        // Active output destination badge with custom Vector Icon + Clean Device Name
        string devName = _audioService.ActiveOutputDeviceName;
        bool isHeadphones = _audioService.IsHeadphoneOrTws;
        AudioDeviceIconPath.Data = isHeadphones
            ? GetVectorGeometry("GeomHeadphones")
            : GetVectorGeometry("GeomSpeakerOn");

        var badgeAccent = IsMonochromeTheme
            ? Color.FromRgb(228, 228, 231)
            : (isHeadphones ? Color.FromRgb(192, 132, 252) : Color.FromRgb(56, 189, 248));
        var badgeFgBrush = BrushCache.Get(badgeAccent);

        AudioDeviceIconPath.Fill = badgeFgBrush;
        AudioDeviceBadgeText.Text = devName;
        AudioDeviceBadgeText.Foreground = badgeFgBrush;
        AudioDeviceBadge.Background = BrushCache.Get(
            IsMonochromeTheme
                ? Color.FromArgb(28, 228, 228, 231)
                : (isHeadphones ? Color.FromArgb(38, 168, 85, 247) : Color.FromArgb(34, 56, 189, 248)));
        AudioDeviceBadge.BorderBrush = BrushCache.Get(
            IsMonochromeTheme
                ? Color.FromArgb(54, 228, 228, 231)
                : (isHeadphones ? Color.FromArgb(65, 168, 85, 247) : Color.FromArgb(60, 56, 189, 248)));

        TimeSpan dur = track.Duration;
        TimeSpan pos = _isDraggingTimeline && dur.TotalSeconds > 0
            ? TimeSpan.FromSeconds(dur.TotalSeconds * _draggedTimelineRatio)
            : _mediaService.GetInterpolatedPosition();

        ElapsedTimeText.Text = FormatTimeSpan(pos);
        TotalDurationText.Text = FormatTimeSpan(dur);

        double trackWidth = Math.Max(0, TimelineTrackBorder.ActualWidth);
        if (dur.TotalSeconds > 0.5 && trackWidth > 0)
        {
            double ratio = _isDraggingTimeline
                ? _draggedTimelineRatio
                : Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0.0, 1.0);
            double fillPx = trackWidth * ratio;
            TimelineFillBorder.Width = fillPx;
        }
        else
        {
            TimelineFillBorder.Width = 0;
        }

        var lyricSnap = _lyricsService.GetActiveLyrics(pos);
        _currentLyricLineProgress = lyricSnap.LineProgressRatio;

        LyricLineProgressTrack.Opacity = lyricSnap.IsSynced ? 1.0 : 0.35;
        LyricLineProgressFill.Width = 160.0 * Math.Clamp(lyricSnap.LineProgressRatio, 0.0, 1.0);

        string compactTargetText;
        string? compactRomanized = null;
        if (_settingsService.LyricsEnabled && lyricSnap.IsSynced && !string.IsNullOrWhiteSpace(lyricSnap.CurrentLine) && lyricSnap.CurrentLine != "• • •")
        {
            compactTargetText = lyricSnap.CurrentLine;
            compactRomanized = lyricSnap.CurrentRomanizedLine;
        }
        else if (_settingsService.LyricsEnabled && lyricSnap.IsSynced && !string.IsNullOrWhiteSpace(lyricSnap.NextLine))
        {
            compactTargetText = lyricSnap.NextLine;
        }
        else
        {
            compactTargetText = track.IsPlaying
                ? $"{track.Title} • {track.Artist}"
                : track.Title;
        }

        bool hasRomanized = !string.IsNullOrWhiteSpace(compactRomanized);
        if (_stateMachine.HasCompactRomanizedSubtitle != hasRomanized)
        {
            _stateMachine.HasCompactRomanizedSubtitle = hasRomanized;
            if (_stateMachine.CurrentState == IslandVisualState.CompactMedia)
            {
                OnIslandStateChanged(_stateMachine.CurrentState, _stateMachine.ActiveTab);
            }
        }

        if (CompactSongRomanizedText != null)
        {
            if (hasRomanized)
            {
                CompactSongRomanizedText.Text = compactRomanized!;
                CompactSongRomanizedText.Visibility = Visibility.Visible;
                Canvas.SetTop(CompactLyricStack, 1.0);
            }
            else
            {
                CompactSongRomanizedText.Text = string.Empty;
                CompactSongRomanizedText.Visibility = Visibility.Collapsed;
                Canvas.SetTop(CompactLyricStack, 8.0);
            }
        }

        if (compactTargetText != _lastDisplayedCompactText || compactRomanized != _lastDisplayedCompactRomanizedText)
        {
            _lastDisplayedCompactText = compactTargetText;
            _lastDisplayedCompactRomanizedText = compactRomanized;
            CompactSongTitleText.Text = compactTargetText;
            _currentPanX = 0;
            CompactLyricTranslate.X = 0;

            int charCount = Math.Max(compactTargetText.Length, compactRomanized?.Length ?? 0);
            _stateMachine.UpdateDynamicCompactWidth(charCount);

            var slideUp = new DoubleAnimation(10.0, 0.0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            var fadeIn = new DoubleAnimation(0.20, 1.0, TimeSpan.FromMilliseconds(280));
            CompactLyricTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
            CompactSongTitleText.BeginAnimation(OpacityProperty, fadeIn);
            if (CompactSongRomanizedText != null && hasRomanized)
            {
                CompactSongRomanizedText.BeginAnimation(OpacityProperty, fadeIn);
            }
        }

        if (lyricSnap.LineIndex != _lastAnimatedLyricIndex || CurrentLyricLineText.Text != lyricSnap.CurrentLine)
        {
            _lastAnimatedLyricIndex = lyricSnap.LineIndex;
            PrevLyricLineText.Text = lyricSnap.PreviousLine;
                        CurrentLyricLineText.Text = lyricSnap.CurrentLine;
            if (CurrentLyricRomanizedText != null)
            {
                if (!string.IsNullOrWhiteSpace(lyricSnap.CurrentRomanizedLine))
                {
                    CurrentLyricRomanizedText.Text = lyricSnap.CurrentRomanizedLine;
                    CurrentLyricRomanizedText.Visibility = Visibility.Visible;
                }
                else
                {
                    CurrentLyricRomanizedText.Visibility = Visibility.Collapsed;
                }
            }
            NextLyricLineText.Text = lyricSnap.NextLine;

            AnimateExpandedLyricsTransition();
        }
    }

    private void AnimateExpandedLyricsTransition()
    {
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        var springBack = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

        var stageSlide = new DoubleAnimation(16.0, 0.0, TimeSpan.FromMilliseconds(360))
        {
            EasingFunction = easeOut
        };
        LyricsStageTranslate.BeginAnimation(TranslateTransform.YProperty, stageSlide);

        var scalePop = new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(400))
        {
            EasingFunction = springBack
        };
        ActiveLyricScale.BeginAnimation(ScaleTransform.ScaleXProperty, scalePop);
        ActiveLyricScale.BeginAnimation(ScaleTransform.ScaleYProperty, scalePop);

        var fade = new DoubleAnimation(0.20, 1.0, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = easeOut
        };
        CurrentLyricLineText.BeginAnimation(OpacityProperty, fade);
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private void OnTimelineMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_mediaService.CurrentTrack.HasActiveSession || _mediaService.CurrentTrack.Duration.TotalSeconds <= 0.5)
            return;

        _isDraggingTimeline = true;
        TimelineScrubberHitArea.CaptureMouse();
        UpdateTimelineRatioFromMouse(e.GetPosition(TimelineTrackBorder).X);
        e.Handled = true;
    }

    private void OnTimelineMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDraggingTimeline) return;
        UpdateTimelineRatioFromMouse(e.GetPosition(TimelineTrackBorder).X);
        e.Handled = true;
    }

    private async void OnTimelineMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDraggingTimeline) return;

        UpdateTimelineRatioFromMouse(e.GetPosition(TimelineTrackBorder).X);
        _isDraggingTimeline = false;
        TimelineScrubberHitArea.ReleaseMouseCapture();

        TimeSpan dur = _mediaService.CurrentTrack.Duration;
        if (dur.TotalSeconds > 0.5)
        {
            TimeSpan target = TimeSpan.FromSeconds(dur.TotalSeconds * _draggedTimelineRatio);
            await _mediaService.SeekToPositionAsync(target);
            UpdateMediaAndLyricsUi();
        }

        e.Handled = true;
    }

    private void UpdateTimelineRatioFromMouse(double mouseX)
    {
        double trackW = Math.Max(1.0, TimelineTrackBorder.ActualWidth);
        _draggedTimelineRatio = Math.Clamp(mouseX / trackW, 0.0, 1.0);
        UpdateMediaAndLyricsUi();
    }

    // --- MOUSE & GESTURE HANDLERS ---

    private async void OnMediaPrevClick(object sender, RoutedEventArgs e) => await _mediaService.SkipPreviousAsync();
    private async void OnMediaPlayPauseClick(object sender, RoutedEventArgs e) => await _mediaService.TogglePlayPauseAsync();
    private async void OnMediaNextClick(object sender, RoutedEventArgs e) => await _mediaService.SkipNextAsync();

    private void OnToggleMediaViewClick(object sender, RoutedEventArgs e)
    {
        _isShowingAppMixer = !_isShowingAppMixer;
        LyricsStageBorder.Visibility = _isShowingAppMixer ? Visibility.Collapsed : Visibility.Visible;
        AppMixerBorder.Visibility = _isShowingAppMixer ? Visibility.Visible : Visibility.Collapsed;

        if (_isShowingAppMixer)
        {
            MediaViewToggleText.Text = "Lirik";
            MediaViewToggleIcon.Data = GetVectorGeometry("GeomMusic");
            UpdateAppMixerUi();
        }
        else
        {
            MediaViewToggleText.Text = "Mixer";
            MediaViewToggleIcon.Data = GetVectorGeometry("GeomAppMixer");
        }
    }

    private void UpdateAppMixerUi(bool force = false)
    {
        if (!_isShowingAppMixer) return;
        if (_isDraggingAppMixer && !force) return;

        var sessions = _audioService.GetAppAudioSessions();
        if (sessions.Count == 0)
        {
            AppMixerItemsControl.ItemsSource = null;
            AppMixerEmptyHintText.Visibility = Visibility.Visible;
            return;
        }

        AppMixerEmptyHintText.Visibility = Visibility.Collapsed;
        var viewModels = new List<AppMixerItemViewModel>();

        const double sliderTrackWidth = 140.0;
        foreach (var s in sessions)
        {
            double fillPx = Math.Clamp((s.VolumePercent / 100.0) * sliderTrackWidth, 0.0, sliderTrackWidth);
            viewModels.Add(new AppMixerItemViewModel
            {
                ProcessId = s.ProcessId,
                AppName = s.AppName,
                VolumePercent = s.VolumePercent,
                FillWidthPx = fillPx,
                IconImage = s.IconImage,
                IsMuted = s.IsMuted,
                MuteIconGeometry = s.IsMuted ? GetVectorGeometry("GeomSpeakerMuted") : GetVectorGeometry("GeomSpeakerOn"),
                MuteIconBrush = s.IsMuted
                    ? BrushCache.Get(Color.FromRgb(248, 113, 113))
                    : (Brush)FindResource("AppAccentBrush")
            });
        }

        AppMixerItemsControl.ItemsSource = viewModels;
    }

    private void OnAppMixerSliderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int pid)
        {
            _isDraggingAppMixer = true;
            _draggingAppMixerPid = pid;
            fe.CaptureMouse();
            ApplyAppMixerSliderFromMouse(fe, e.GetPosition(fe).X, pid);
            e.Handled = true;
        }
    }

    private void OnAppMixerSliderMouseMove(object sender, MouseEventArgs e)
    {
        if (_isDraggingAppMixer && e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement fe && fe.Tag is int pid)
        {
            ApplyAppMixerSliderFromMouse(fe, e.GetPosition(fe).X, pid);
            e.Handled = true;
        }
    }

    private void OnAppMixerSliderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingAppMixer)
        {
            _isDraggingAppMixer = false;
            (sender as UIElement)?.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void ApplyAppMixerSliderFromMouse(FrameworkElement fe, double mouseX, int pid)
    {
        double trackW = Math.Max(1.0, fe.ActualWidth);
        int pct = (int)Math.Round(Math.Clamp(mouseX / trackW, 0.0, 1.0) * 100);
        _audioService.SetAppVolume(pid, pct);
        UpdateAppMixerUi(force: true);
    }

    private void OnToggleAppMixerMuteClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int pid)
        {
            _audioService.ToggleAppMute(pid);
            UpdateAppMixerUi();
            e.Handled = true;
        }
    }

    private void OnAudioDeviceBadgeClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (AudioDevicePickerBorder.Visibility == Visibility.Visible)
        {
            AudioDevicePickerBorder.Visibility = Visibility.Collapsed;
            return;
        }

        RefreshAudioDevicePickerList();
        AudioDevicePickerBorder.Visibility = Visibility.Visible;
    }

    private void OnCloseAudioDevicePickerClick(object sender, RoutedEventArgs e)
    {
        AudioDevicePickerBorder.Visibility = Visibility.Collapsed;
    }

    private void RefreshAudioDevicePickerList()
    {
        var devices = _audioService.GetOutputDevices();
        bool mono = IsMonochromeTheme;
        var items = new List<AudioDevicePickerItemViewModel>(devices.Count);
        foreach (var d in devices)
        {
            Color accentColor = mono
                ? (d.IsDefault ? Color.FromRgb(244, 244, 245) : Color.FromRgb(161, 161, 170))
                : (d.IsDefault ? Color.FromRgb(34, 197, 94) : Color.FromRgb(56, 189, 248));

            items.Add(new AudioDevicePickerItemViewModel
            {
                DeviceId = d.Id,
                FriendlyName = d.Name,
                IsDefault = d.IsDefault,
                IconGeometry = GetVectorGeometry(d.IsHeadphone ? "GeomHeadphones" : "GeomSpeakerOn"),
                AccentBrush = BrushCache.Get(accentColor),
                RowBackground = d.IsDefault ? BrushCache.Get(Color.FromArgb(0x22, accentColor.R, accentColor.G, accentColor.B)) : BrushCache.Get(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                RowBorderBrush = d.IsDefault ? BrushCache.Get(Color.FromArgb(0x55, accentColor.R, accentColor.G, accentColor.B)) : BrushCache.Get(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                ActiveCheckVisibility = d.IsDefault ? Visibility.Visible : Visibility.Collapsed
            });
        }
        AudioDevicePickerList.ItemsSource = items;
    }

    private void OnSelectAudioDeviceItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string devId && !string.IsNullOrWhiteSpace(devId))
        {
            if (_audioService.SetDefaultOutputDevice(devId))
            {
                AudioDevicePickerBorder.Visibility = Visibility.Collapsed;
                UpdateSlidersAndMuteUi();
                UpdateMediaAndLyricsUi();
                TriggerToast(new ToastPayload(
                    Icon: _audioService.IsHeadphoneOrTws ? "HEADPHONES" : "SPEAKER",
                    Title: LocalizationService.Instance.Get("Audio_SwitchedToast", "Audio Output Switched"),
                    Subtitle: _audioService.ActiveOutputDeviceName,
                    AccentHex: "#22C55E",
                    Duration: TimeSpan.FromSeconds(2.2)));
            }
        }
    }

    // =========================================================================
    // FEATURE 4 & 5: LAN WI-FI QR SHARE + SCREEN OCR & EYEDROPPER TOOLS
    // =========================================================================
}
