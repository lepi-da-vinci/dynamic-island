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
/// 60fps morph loop, Bezier notch contour geometry builder, and compact lyric panning.
/// </summary>
public partial class MainWindow
{
    private void EnsureRenderLoopRunning()
    {
        if (_isRenderLoopAttached) return;
        _lastRenderTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnCompositionTargetRendering;
        _isRenderLoopAttached = true;
    }

    private void StopRenderLoop()
    {
        if (!_isRenderLoopAttached) return;
        CompositionTarget.Rendering -= OnCompositionTargetRendering;
        _isRenderLoopAttached = false;
    }

    private void OnCompositionTargetRendering(object? sender, EventArgs e)
    {
        try
        {
            long now = Stopwatch.GetTimestamp();
            double dt = Math.Clamp((double)(now - _lastRenderTick) / Stopwatch.Frequency, 0.001, 0.05);
            _lastRenderTick = now;

            bool morphActive = _morph.Update(dt);

            // Smooth satellite side-wing width animations (Zero notch drift, rock-solid center)
            double wingSmooth = Math.Clamp(dt * 16.0, 0.05, 0.35);
            _leftWingWidth += (_targetLeftWingWidth - _leftWingWidth) * wingSmooth;
            _rightWingWidth += (_targetRightWingWidth - _rightWingWidth) * wingSmooth;

            if (Math.Abs(_targetLeftWingWidth - _leftWingWidth) < 0.1 && _targetLeftWingWidth == 0) _leftWingWidth = 0;
            if (Math.Abs(_targetRightWingWidth - _rightWingWidth) < 0.1 && _targetRightWingWidth == 0) _rightWingWidth = 0;

            if (_leftWingWidth > 1.0)
            {
                LeftWingContainer.Visibility = Visibility.Visible;
                LeftWingContainer.Opacity = Math.Clamp((_leftWingWidth - 6.0) / 45.0, 0.0, 1.0);
                LeftWingTranslate.X = (56.0 - _leftWingWidth) * 0.35;
                LeftWingContainer.IsHitTestVisible = _leftWingWidth > 24.0;
            }
            else
            {
                LeftWingContainer.Visibility = Visibility.Collapsed;
                LeftWingContainer.Opacity = 0.0;
                LeftWingTranslate.X = 0;
                LeftWingContainer.IsHitTestVisible = false;
            }

            if (_rightWingWidth > 1.0)
            {
                RightWingContainer.Visibility = Visibility.Visible;
                RightWingContainer.Opacity = Math.Clamp((_rightWingWidth - 6.0) / 45.0, 0.0, 1.0);
                RightWingTranslate.X = (_rightWingWidth - 56.0) * 0.35;
                RightWingContainer.IsHitTestVisible = _rightWingWidth > 24.0;
            }
            else
            {
                RightWingContainer.Visibility = Visibility.Collapsed;
                RightWingContainer.Opacity = 0.0;
                RightWingTranslate.X = 0;
                RightWingContainer.IsHitTestVisible = false;
            }

            double targetYTop = _settingsService.IslandPlacementStyle == "PunchHole"
                ? (_stateMachine.CurrentState == IslandVisualState.TuckedTopEdge ? 4.0 : 8.0)
                : 0.0;

            if (_stateMachine.IsScreenshotMode)
            {
                _currentPlacementTopDip = targetYTop;
            }
            else
            {
                _currentPlacementTopDip += (targetYTop - _currentPlacementTopDip) * Math.Clamp(dt * 15.0, 0.08, 0.45);
                if (Math.Abs(targetYTop - _currentPlacementTopDip) < 0.05)
                {
                    _currentPlacementTopDip = targetYTop;
                }
            }

            double yTop = _currentPlacementTopDip;
            bool isPunchHole = _settingsService.IslandPlacementStyle == "PunchHole" || yTop > 0.4;

            double bodyWidth = Math.Max(48, _morph.Width.Current);
            double height = Math.Max(4, _morph.Height.Current);
            double bottomRadius = Math.Clamp(_morph.BottomCornerRadius.Current, 2, height * 0.65);
            double topEarRadius = Math.Clamp(_morph.TopEarRadius.Current, 0, height * 0.55);

            double contentLeft = (MasterGridWidth - bodyWidth) / 2.0;
            IslandBorder.Margin = new Thickness(contentLeft, yTop, 0, 0);
            IslandBorder.Width = bodyWidth;
            IslandBorder.Height = height;

            double wingY = Math.Max(0.0, yTop + (height - 120.0) / 2.0);
            LeftWingContainer.Margin = new Thickness(contentLeft - 56.0, wingY, 0, 0);
            RightWingContainer.Margin = new Thickness(contentLeft + bodyWidth, wingY, 0, 0);

            double xLeftBody = contentLeft - _leftWingWidth;
            double xRightBody = contentLeft + bodyWidth + _rightWingWidth;

            AmbientGlowBorder.Margin = new Thickness(xLeftBody, yTop, 0, 0);
            AmbientGlowBorder.Width = Math.Max(10, xRightBody - xLeftBody);
            AmbientGlowBorder.Height = height;
            AmbientGlowBorder.CornerRadius = isPunchHole
                ? new CornerRadius(bottomRadius)
                : new CornerRadius(0, 0, bottomRadius, bottomRadius);
            AmbientGlowBorder.Opacity = Math.Clamp(_morph.GlowOpacity.Current * _settingsService.GlowIntensity, 0.0, 1.0);

            UpdateUnifiedNotchGeometry(xLeftBody, xRightBody, yTop, height, bottomRadius, topEarRadius, isPunchHole);
            UpdateWindowClickThroughState();

            bool isCompactMedia =
                _stateMachine.CurrentState == IslandVisualState.CompactMedia &&
                _mediaService.CurrentTrack.HasActiveSession;

            bool isExpandedMedia =
                _stateMachine.CurrentState == IslandVisualState.ExpandedDashboard &&
                _stateMachine.ActiveTab == IslandDashboardTab.MediaLyrics &&
                _mediaService.CurrentTrack.HasActiveSession;

            if (isCompactMedia)
            {
                if (_settingsService.AudioVisualizerEnabled)
                {
                    _audioService.UpdateSpectrumBars(_spectrumHeights, _mediaService.CurrentTrack.IsPlaying, maxBarHeight: 18.0);
                    for (int i = 0; i < _waveRectangles.Length; i++)
                    {
                        _waveRectangles[i].Height = _spectrumHeights[i];
                    }
                }

                if (_settingsService.SlowLyricPanEnabled)
                {
                    UpdateCompactLyricSlowPan(dt);
                }
            }
            else if (isExpandedMedia)
            {
                // Drive real-time WASAPI Loopback 5-band waveform in Expanded Music Player
                if (_settingsService.AudioVisualizerEnabled)
                {
                    _audioService.UpdateSpectrumBars(_spectrumHeights, _mediaService.CurrentTrack.IsPlaying, maxBarHeight: 15.0);
                    for (int i = 0; i < _expWaveRectangles.Length; i++)
                    {
                        _expWaveRectangles[i].Height = _spectrumHeights[i];
                    }
                }
            }

            bool wingsActive = Math.Abs(_targetLeftWingWidth - _leftWingWidth) > 0.05 ||
                               Math.Abs(_targetRightWingWidth - _rightWingWidth) > 0.05 ||
                               _leftWingWidth > 0.05 ||
                               _rightWingWidth > 0.05;
            bool placementAnimating = Math.Abs(targetYTop - _currentPlacementTopDip) > 0.05;

            bool mediaAnimating = false;
            if (isCompactMedia || isExpandedMedia)
            {
                if (_mediaService.CurrentTrack.IsPlaying)
                {
                    mediaAnimating = true;
                }
                else
                {
                    bool barsResting = _audioService.AreBarsResting(_spectrumHeights);
                    bool panResting = Math.Abs(_currentPanX) < 0.1;
                    mediaAnimating = !barsResting || !panResting;
                }
            }

            if (!morphActive && !wingsActive && !placementAnimating && !mediaAnimating)
            {
                StopRenderLoop();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("RenderLoop", "Error during OnCompositionTargetRendering", ex);
        }
    }

    private void UpdateUnifiedNotchGeometry(
        double xLeftBody,
        double xRightBody,
        double yTop,
        double height,
        double bottomRadius,
        double topEarRadius,
        bool isPunchHole)
    {
        if (isPunchHole)
        {
            double capsuleWidth = Math.Max(4.0, xRightBody - xLeftBody);
            double capsuleRadius = Math.Min(bottomRadius, Math.Min(capsuleWidth * 0.5, height * 0.5));
            var capsuleGeom = new RectangleGeometry(
                new Rect(xLeftBody, yTop, capsuleWidth, height),
                capsuleRadius,
                capsuleRadius);
            capsuleGeom.Freeze();

            UnifiedNotchPath.Data = capsuleGeom;
            UnifiedNotchRimPath.Data = capsuleGeom;
            return;
        }

        topEarRadius = Math.Max(2.0, topEarRadius);
        if (topEarRadius + bottomRadius > height)
        {
            double scale = height / Math.Max(1.0, topEarRadius + bottomRadius);
            topEarRadius *= scale;
            bottomRadius *= scale;
        }

        double xLeftEar = Math.Max(0.0, xLeftBody - topEarRadius);
        double xRightEar = Math.Min(MasterGridWidth, xRightBody + topEarRadius);

        var fillGeom = new StreamGeometry();
        using (StreamGeometryContext ctx = fillGeom.Open())
        {
            BuildNotchContourDirect(ctx, xLeftEar, xLeftBody, xRightBody, xRightEar, height, topEarRadius, bottomRadius, isFilled: true, isClosed: true);
        }
        fillGeom.Freeze();

        UnifiedNotchPath.Data = fillGeom;

        var rimGeom = new StreamGeometry();
        using (StreamGeometryContext ctx = rimGeom.Open())
        {
            BuildNotchContourDirect(ctx, xLeftEar, xLeftBody, xRightBody, xRightEar, height, topEarRadius, bottomRadius, isFilled: false, isClosed: false);
        }
        rimGeom.Freeze();

        UnifiedNotchRimPath.Data = rimGeom;
    }

    private static void BuildNotchContourDirect(
        StreamGeometryContext ctx,
        double xLeftEar,
        double xLeftBody,
        double xRightBody,
        double xRightEar,
        double height,
        double topEarRadius,
        double bottomRadius,
        bool isFilled,
        bool isClosed)
    {
        ctx.BeginFigure(new Point(xLeftEar, 0), isFilled: isFilled, isClosed: isClosed);

        ctx.QuadraticBezierTo(
            new Point(xLeftBody, 0),
            new Point(xLeftBody, topEarRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.LineTo(
            new Point(xLeftBody, height - bottomRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.QuadraticBezierTo(
            new Point(xLeftBody, height),
            new Point(xLeftBody + bottomRadius, height),
            isStroked: true,
            isSmoothJoin: true);

        ctx.LineTo(
            new Point(xRightBody - bottomRadius, height),
            isStroked: true,
            isSmoothJoin: true);

        ctx.QuadraticBezierTo(
            new Point(xRightBody, height),
            new Point(xRightBody, height - bottomRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.LineTo(
            new Point(xRightBody, topEarRadius),
            isStroked: true,
            isSmoothJoin: true);

        ctx.QuadraticBezierTo(
            new Point(xRightBody, 0),
            new Point(xRightEar, 0),
            isStroked: true,
            isSmoothJoin: true);
    }

    private void UpdateCompactLyricSlowPan(double dt)
    {
        double textWidth = CompactSongTitleText.ActualWidth;
        if (CompactSongRomanizedText != null && CompactSongRomanizedText.Visibility == Visibility.Visible)
        {
            textWidth = Math.Max(textWidth, CompactSongRomanizedText.ActualWidth);
        }
        double viewportWidth = CompactLyricViewport.ActualWidth;

        if (viewportWidth <= 10 || textWidth <= viewportWidth + 2)
        {
            _currentPanX += (0 - _currentPanX) * Math.Min(1.0, dt * 8.0);
            CompactLyricTranslate.X = _currentPanX;
            return;
        }

        double maxOverflow = textWidth - viewportWidth + 10;
        double normalizedPan = Math.Clamp((_currentLyricLineProgress - 0.15) / 0.73, 0.0, 1.0);
        double targetX = -maxOverflow * normalizedPan;

        _currentPanX += (targetX - _currentPanX) * Math.Min(1.0, dt * 4.5);
        CompactLyricTranslate.X = _currentPanX;
    }
}
