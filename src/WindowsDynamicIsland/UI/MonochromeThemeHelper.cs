using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace WindowsDynamicIsland.UI;

internal static class MonochromeThemeHelper
{
    private readonly record struct BrushPropSnapshot(
        DependencyObject Target,
        DependencyProperty Property,
        SolidColorBrush OriginalBrush);

    private readonly record struct ShadowSnapshot(
        DropShadowEffect Effect,
        Color OriginalColor);

    private sealed class TreeSnapshotState
    {
        public bool Captured;
        public readonly List<BrushPropSnapshot> Brushes = [];
        public readonly List<ShadowSnapshot> Shadows = [];
    }

    private static readonly DependencyProperty SnapshotStateProperty =
        DependencyProperty.RegisterAttached(
            "SnapshotState",
            typeof(TreeSnapshotState),
            typeof(MonochromeThemeHelper),
            new PropertyMetadata(null));

    private static readonly DependencyProperty[] BrushProperties =
    [
        Border.BackgroundProperty,
        Border.BorderBrushProperty,
        Panel.BackgroundProperty,
        Control.BackgroundProperty,
        Control.BorderBrushProperty,
        Control.ForegroundProperty,
        TextBlock.ForegroundProperty,
        TextBoxBase.CaretBrushProperty,
        TextBoxBase.SelectionBrushProperty,
        Shape.FillProperty,
        Shape.StrokeProperty
    ];

    public static void ApplyToSubtree(
        DependencyObject root,
        bool isMonochrome,
        HashSet<string>? excludedElementNames = null)
    {
        if (root == null) return;

        var state = (TreeSnapshotState?)root.GetValue(SnapshotStateProperty);
        if (state == null)
        {
            state = new TreeSnapshotState();
            root.SetValue(SnapshotStateProperty, state);
        }

        if (!state.Captured)
        {
            CaptureSubtree(root, state, excludedElementNames);
            state.Captured = true;
        }

        if (isMonochrome)
        {
            foreach (var entry in state.Brushes)
            {
                bool isOpaqueButtonBg = entry.Target is Button &&
                                        entry.Property == Control.BackgroundProperty &&
                                        entry.OriginalBrush.Color.A >= 220;

                Color mono = ToMonochromeColor(entry.OriginalBrush.Color, isOpaqueButtonBg);
                var brush = new SolidColorBrush(mono);
                brush.Freeze();
                entry.Target.SetValue(entry.Property, brush);
            }

            foreach (var shadow in state.Shadows)
            {
                if (!shadow.Effect.IsFrozen)
                {
                    Color c = shadow.OriginalColor;
                    byte lum = (byte)Math.Clamp((int)Math.Round(0.299 * c.R + 0.587 * c.G + 0.114 * c.B), 0, 180);
                    shadow.Effect.Color = Color.FromArgb(c.A, lum, lum, lum);
                }
            }
        }
        else
        {
            foreach (var entry in state.Brushes)
            {
                entry.Target.SetValue(entry.Property, entry.OriginalBrush);
            }

            foreach (var shadow in state.Shadows)
            {
                if (!shadow.Effect.IsFrozen)
                {
                    shadow.Effect.Color = shadow.OriginalColor;
                }
            }
        }
    }

    private static void CaptureSubtree(
        DependencyObject node,
        TreeSnapshotState state,
        HashSet<string>? excludedNames)
    {
        if (node is FrameworkElement fe &&
            !string.IsNullOrEmpty(fe.Name) &&
            excludedNames != null &&
            excludedNames.Contains(fe.Name))
        {
            return;
        }

        foreach (DependencyProperty dp in BrushProperties)
        {
            object localVal = node.ReadLocalValue(dp);
            if (localVal is SolidColorBrush scb)
            {
                Color c = scb.Color;
                int spread = Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
                if (c.A > 0 && spread > 4)
                {
                    state.Brushes.Add(new BrushPropSnapshot(node, dp, scb));
                }
            }
        }

        if (node is UIElement uie && uie.Effect is DropShadowEffect dse)
        {
            Color sc = dse.Color;
            int spread = Math.Max(sc.R, Math.Max(sc.G, sc.B)) - Math.Min(sc.R, Math.Min(sc.G, sc.B));
            if (spread > 4)
            {
                state.Shadows.Add(new ShadowSnapshot(dse, sc));
            }
        }

        foreach (object? child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject depChild)
            {
                CaptureSubtree(depChild, state, excludedNames);
            }
        }
    }

    public static Color ToMonochromeColor(Color c, bool isOpaqueButtonBg = false)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        int spread = max - min;

        if (spread <= 4)
        {
            return c;
        }

        if (isOpaqueButtonBg)
        {
            // Keep opaque hero action buttons dark zinc (#3F3F46) so white text/icons stay crisp
            return Color.FromArgb(c.A, 63, 63, 70);
        }

        // Translucent tinted glass surfaces/borders (e.g., #2038BDF8, #2822C55E) -> Frosted Zinc-200
        if (c.A < 205 && spread >= 40)
        {
            return Color.FromArgb(c.A, 228, 228, 231);
        }

        // Saturated foreground icons, sliders, rings, and accent labels -> Crisp Apple Silver (#E4E4E7)
        if (spread >= 55 && max >= 150)
        {
            return Color.FromArgb(c.A, 228, 228, 231);
        }

        // Cool slate grays (#64748B, #94A3B8, #CBD5E1) -> Pure neutral perceptual grayscale
        byte lum = (byte)Math.Clamp(
            (int)Math.Round(0.299 * c.R + 0.587 * c.G + 0.114 * c.B),
            0,
            255);
        return Color.FromArgb(c.A, lum, lum, lum);
    }
}

