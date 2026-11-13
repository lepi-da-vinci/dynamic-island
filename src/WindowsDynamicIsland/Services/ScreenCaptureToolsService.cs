using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WindowsDynamicIsland.Services;

public sealed record CapturedDesktopFrame(
    int LeftPx,
    int TopPx,
    int WidthPx,
    int HeightPx,
    byte[] BgraPixels,
    BitmapSource Bitmap);

/// <summary>
/// Pure Win32 GDI + WinRT Windows.Media.Ocr capture helper with zero System.Drawing dependencies.
/// Supports virtual-screen snapshots, pixel color sampling, and region OCR text extraction.
/// </summary>
public static class ScreenCaptureToolsService
{
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SRCCOPY = 0x00CC0020;

    public static CapturedDesktopFrame? CaptureVirtualScreen()
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width <= 0 || height <= 0) return null;

        IntPtr screenDc = IntPtr.Zero;
        IntPtr memDc = IntPtr.Zero;
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return null;

            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) return null;

            hBitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (hBitmap == IntPtr.Zero) return null;

            oldBitmap = SelectObject(memDc, hBitmap);
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, left, top, SRCCOPY))
            {
                return null;
            }

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down DIB
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0 // BI_RGB
                }
            };

            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            if (GetDIBits(memDc, hBitmap, 0, (uint)height, pixels, ref bmi, 0) == 0)
            {
                return null;
            }

            // Force opaque alpha channel in captured desktop buffer for crisp WPF display and WinRT OCR
            for (int i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }

            var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            bmp.Freeze();

            return new CapturedDesktopFrame(left, top, width, height, pixels, bmp);
        }
        catch (Exception ex)
        {
            AppLogger.LogError("ScreenCaptureTools", "Failed capturing virtual screen", ex);
            return null;
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static Color SamplePixel(CapturedDesktopFrame frame, int localX, int localY)
    {
        int x = Math.Clamp(localX, 0, frame.WidthPx - 1);
        int y = Math.Clamp(localY, 0, frame.HeightPx - 1);
        int idx = ((y * frame.WidthPx) + x) * 4;
        byte b = frame.BgraPixels[idx];
        byte g = frame.BgraPixels[idx + 1];
        byte r = frame.BgraPixels[idx + 2];
        return Color.FromRgb(r, g, b);
    }

    public static async Task<string> RecognizeRegionAsync(CapturedDesktopFrame frame, Int32Rect cropPx)
    {
        int x = Math.Clamp(cropPx.X, 0, frame.WidthPx - 1);
        int y = Math.Clamp(cropPx.Y, 0, frame.HeightPx - 1);
        int w = Math.Clamp(cropPx.Width, 1, frame.WidthPx - x);
        int h = Math.Clamp(cropPx.Height, 1, frame.HeightPx - y);

        if (w < 4 || h < 4) return string.Empty;

        try
        {
            int srcStride = frame.WidthPx * 4;
            int dstStride = w * 4;
            byte[] cropped = new byte[dstStride * h];

            for (int row = 0; row < h; row++)
            {
                Buffer.BlockCopy(frame.BgraPixels, ((y + row) * srcStride) + (x * 4), cropped, row * dstStride, dstStride);
            }

            var ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (ocrEngine == null) return string.Empty;

            using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                cropped.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                w,
                h,
                BitmapAlphaMode.Premultiplied);

            var result = await ocrEngine.RecognizeAsync(softwareBitmap);
            if (result == null || result.Lines.Count == 0) return string.Empty;

            return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text.Trim()).Where(t => !string.IsNullOrEmpty(t)));
        }
        catch (Exception ex)
        {
            AppLogger.LogError("ScreenCaptureTools", "OCR recognition failed", ex);
            return string.Empty;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, [Out] byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }
}
