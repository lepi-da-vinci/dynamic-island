using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WindowsDynamicIsland.Core;
using WindowsDynamicIsland.Services;

namespace WindowsDynamicIsland.UI;

public enum ScreenToolMode
{
    OcrSnip,
    ColorPicker
}

public partial class ScreenSnipOverlayWindow : Window
{
    private readonly CapturedDesktopFrame _frame;
    private readonly ScreenToolMode _mode;
    private readonly double _scaleX;
    private readonly double _scaleY;
    private bool _isDragging;
    private Point _dragStartDip;

    public string? ExtractedOcrText { get; private set; }
    public string? PickedColorHex { get; private set; }
    public bool Completed { get; private set; }

    public ScreenSnipOverlayWindow(CapturedDesktopFrame frame, ScreenToolMode mode)
    {
        _frame = frame;
        _mode = mode;

        InitializeComponent();

        var primaryMon = Win32Interop.ResolveTargetMonitor("Primary");
        double dpiScale = primaryMon?.ScaleFactor ?? 1.0;
        _scaleX = dpiScale;
        _scaleY = dpiScale;

        Left = frame.LeftPx / dpiScale;
        Top = frame.TopPx / dpiScale;
        Width = frame.WidthPx / dpiScale;
        Height = frame.HeightPx / dpiScale;

        BackdropImage.Source = frame.Bitmap;

        bool en = LocalizationService.Instance.IsEnglish;
        if (mode == ScreenToolMode.ColorPicker)
        {
            InstructionText.Text = en
                ? "Click any pixel to copy HEX color • Right-click or ESC to cancel"
                : "Klik piksel mana saja untuk menyalin warna HEX • Klik kanan / ESC batal";
            EyedropperCard.Visibility = Visibility.Visible;
            SelectionDimMask.Visibility = Visibility.Collapsed;
        }
        else
        {
            InstructionText.Text = en
                ? "Drag screen area to copy text (OCR) • Right-click or ESC to cancel"
                : "Tarik area layar untuk menyalin teks (OCR) • Klik kanan / ESC batal";
            UpdateSelectionGeometry(Rect.Empty);
        }

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            Win32Interop.SetWindowPos(
                hwnd,
                Win32Interop.HWND_TOPMOST,
                _frame.LeftPx,
                _frame.TopPx,
                _frame.WidthPx,
                _frame.HeightPx,
                Win32Interop.SWP_SHOWWINDOW);
        };
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Completed = false;
            Close();
        }
    }

    private void OnOverlayRightClick(object sender, MouseButtonEventArgs e)
    {
        Completed = false;
        Close();
    }

    private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        Point pos = e.GetPosition(OverlayCanvas);
        if (_mode == ScreenToolMode.ColorPicker)
        {
            var (px, py) = DipToFramePixel(pos);
            Color c = ScreenCaptureToolsService.SamplePixel(_frame, px, py);
            PickedColorHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            try { Clipboard.SetText(PickedColorHex); } catch { }
            Completed = true;
            Close();
            return;
        }

        _isDragging = true;
        _dragStartDip = pos;
        CaptureMouse();
        SelectionBox.Visibility = Visibility.Visible;
        SizePill.Visibility = Visibility.Visible;
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        Point pos = e.GetPosition(OverlayCanvas);

        if (_mode == ScreenToolMode.ColorPicker)
        {
            UpdateEyedropperPreview(pos);
            return;
        }

        if (!_isDragging) return;

        double x = Math.Min(_dragStartDip.X, pos.X);
        double y = Math.Min(_dragStartDip.Y, pos.Y);
        double w = Math.Abs(pos.X - _dragStartDip.X);
        double h = Math.Abs(pos.Y - _dragStartDip.Y);
        var rect = new Rect(x, y, w, h);

        Canvas.SetLeft(SelectionBox, rect.X);
        Canvas.SetTop(SelectionBox, rect.Y);
        SelectionBox.Width = rect.Width;
        SelectionBox.Height = rect.Height;
        UpdateSelectionGeometry(rect);

        int pxW = (int)Math.Round(rect.Width * (_frame.WidthPx / Math.Max(1.0, ActualWidth)));
        int pxH = (int)Math.Round(rect.Height * (_frame.HeightPx / Math.Max(1.0, ActualHeight)));
        SizePillText.Text = $"{pxW} x {pxH} px";

        double pillTop = rect.Y >= 32 ? rect.Y - 26 : rect.Bottom + 6;
        Canvas.SetLeft(SizePill, rect.X);
        Canvas.SetTop(SizePill, pillTop);
    }

    private async void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode != ScreenToolMode.OcrSnip || !_isDragging) return;

        _isDragging = false;
        ReleaseMouseCapture();

        Point pos = e.GetPosition(OverlayCanvas);
        double x = Math.Min(_dragStartDip.X, pos.X);
        double y = Math.Min(_dragStartDip.Y, pos.Y);
        double w = Math.Abs(pos.X - _dragStartDip.X);
        double h = Math.Abs(pos.Y - _dragStartDip.Y);

        if (w < 8 || h < 8)
        {
            Completed = false;
            Close();
            return;
        }

        double ratioX = _frame.WidthPx / Math.Max(1.0, ActualWidth);
        double ratioY = _frame.HeightPx / Math.Max(1.0, ActualHeight);
        var cropPx = new Int32Rect(
            (int)Math.Round(x * ratioX),
            (int)Math.Round(y * ratioY),
            (int)Math.Round(w * ratioX),
            (int)Math.Round(h * ratioY));

        Hide();
        ExtractedOcrText = await ScreenCaptureToolsService.RecognizeRegionAsync(_frame, cropPx);
        if (!string.IsNullOrWhiteSpace(ExtractedOcrText))
        {
            try { Clipboard.SetText(ExtractedOcrText); } catch { }
        }

        Completed = true;
        Close();
    }

    private (int X, int Y) DipToFramePixel(Point dip)
    {
        double ratioX = ActualWidth > 1 ? _frame.WidthPx / ActualWidth : _scaleX;
        double ratioY = ActualHeight > 1 ? _frame.HeightPx / ActualHeight : _scaleY;
        return (
            Math.Clamp((int)Math.Round(dip.X * ratioX), 0, _frame.WidthPx - 1),
            Math.Clamp((int)Math.Round(dip.Y * ratioY), 0, _frame.HeightPx - 1));
    }

    private void UpdateEyedropperPreview(Point cursorDip)
    {
        var (px, py) = DipToFramePixel(cursorDip);
        Color sampled = ScreenCaptureToolsService.SamplePixel(_frame, px, py);

        HexReadoutText.Text = $"#{sampled.R:X2}{sampled.G:X2}{sampled.B:X2}";
        RgbReadoutText.Text = $"{sampled.R}, {sampled.G}, {sampled.B}";
        ColorSwatchBorder.Background = BrushCache.Get(sampled);

        const int halfW = 14;
        const int halfH = 8;
        int cropX = Math.Clamp(px - halfW, 0, Math.Max(0, _frame.WidthPx - (halfW * 2)));
        int cropY = Math.Clamp(py - halfH, 0, Math.Max(0, _frame.HeightPx - (halfH * 2)));
        int cropW = Math.Min(halfW * 2, _frame.WidthPx - cropX);
        int cropH = Math.Min(halfH * 2, _frame.HeightPx - cropY);

        if (cropW > 0 && cropH > 0)
        {
            var crop = new CroppedBitmap(_frame.Bitmap, new Int32Rect(cropX, cropY, cropW, cropH));
            crop.Freeze();
            MagnifierImage.Source = crop;
        }

        double maxW = Math.Max(200, ActualWidth);
        double maxH = Math.Max(200, ActualHeight);
        double left = cursorDip.X + 20;
        double top = cursorDip.Y + 20;
        if (left + 168 > maxW) left = cursorDip.X - 174;
        if (top + 132 > maxH) top = cursorDip.Y - 138;

        Canvas.SetLeft(EyedropperCard, Math.Max(6, left));
        Canvas.SetTop(EyedropperCard, Math.Max(6, top));
    }

    private void UpdateSelectionGeometry(Rect selection)
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        var fullScreen = new RectangleGeometry(new Rect(0, 0, w, h));

        if (selection.IsEmpty || selection.Width <= 0 || selection.Height <= 0)
        {
            SelectionDimMask.Data = fullScreen;
            return;
        }

        var hole = new RectangleGeometry(selection);
        SelectionDimMask.Data = new CombinedGeometry(GeometryCombineMode.Exclude, fullScreen, hole);
    }
}
