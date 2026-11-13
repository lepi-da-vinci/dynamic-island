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
/// Quick Spotlight search launcher and island collection item ViewModels.
/// </summary>
public partial class MainWindow
{
    public void ToggleSpotlightMode()
    {
        if (_stateMachine.IsSpotlightActive)
        {
            CloseSpotlight();
        }
        else
        {
            OpenSpotlight();
        }
    }

    public void OpenSpotlight()
    {
        _stateMachine.SetSpotlightActive(true);
        SpotlightSearchBox.Text = string.Empty;
        SpotlightPlaceholder.Visibility = Visibility.Visible;
        SpotlightClearBtn.Visibility = Visibility.Collapsed;
        _selectedSpotlightIndex = -1;
        UpdateSpotlightSearchResults(string.Empty);
        ForceActivateAndFocus(SpotlightSearchBox);
    }

    public void CloseSpotlight()
    {
        _stateMachine.SetSpotlightActive(false);
        Keyboard.ClearFocus();
        SetWindowActivatable(false);
    }

    private void OnSpotlightClearClick(object sender, RoutedEventArgs e)
    {
        SpotlightSearchBox.Text = string.Empty;
        SpotlightSearchBox.Focus();
    }

    private void OnSpotlightSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        string query = SpotlightSearchBox.Text ?? string.Empty;
        SpotlightPlaceholder.Visibility = string.IsNullOrEmpty(query) ? Visibility.Visible : Visibility.Collapsed;
        SpotlightClearBtn.Visibility = string.IsNullOrEmpty(query) ? Visibility.Collapsed : Visibility.Visible;

        _selectedSpotlightIndex = 0;
        UpdateSpotlightSearchResults(query);
    }

    private void OnSpotlightSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseSpotlight();
            return;
        }

        if (_currentSpotlightItems.Count == 0) return;

        if (e.Key == Key.Down)
        {
            e.Handled = true;
            _selectedSpotlightIndex = (_selectedSpotlightIndex + 1) % _currentSpotlightItems.Count;
            RefreshSpotlightItemSelection();
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            _selectedSpotlightIndex = (_selectedSpotlightIndex - 1 + _currentSpotlightItems.Count) % _currentSpotlightItems.Count;
            RefreshSpotlightItemSelection();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ExecuteSelectedSpotlightItem();
        }
    }

    private void OnSpotlightResultItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement elem && elem.Tag is string itemId)
        {
            var match = _currentSpotlightItems.FirstOrDefault(x => x.Id == itemId);
            if (match != null)
            {
                ExecuteSpotlightItem(match);
            }
        }
    }

    private void UpdateSpotlightSearchResults(string rawQuery)
    {
        _currentSpotlightItems = _spotlightService.Search(
            rawQuery,
            toggleMuteAudio: () => _audioService.ToggleMute(),
            toggleMuteMic: () => _audioService.ToggleMicrophoneMute(),
            toggleBluetooth: () => _ = _hardwareService.ToggleBluetoothRadioAsync(),
            openSettings: () => OpenSettingsWindow(0),
            tuckIsland: () => _stateMachine.SetTuckedMode(true),
            startPomodoroFocus: () => _pomodoroService.StartFocus(),
                        sleepSystem: () => SystemPowerService.Sleep(),
            hibernateSystem: () => SystemPowerService.Hibernate(),
            openClipboardHistory: () => SwitchToClipboardTab(),
            launchOcrSnip: LaunchOcrSnipTool,
            launchColorPicker: LaunchColorPickerTool);

        RefreshSpotlightItemSelection();

        if (_currentSpotlightItems.Count == 0)
        {
            SpotlightResultsBorder.Visibility = Visibility.Collapsed;
            _stateMachine.SetSpotlightHeight(64.0);
        }
        else
        {
            SpotlightResultsBorder.Visibility = Visibility.Visible;
            double listHeight = Math.Min(220.0, _currentSpotlightItems.Count * 44.0 + 12.0);
            _stateMachine.SetSpotlightHeight(64.0 + listHeight + 8.0);
        }
    }

    private void RefreshSpotlightItemSelection()
    {
        var accentBrush = (Brush)FindResource("AppAccentBrush");
        var viewModels = new List<SpotlightItemViewModel>();

        for (int i = 0; i < _currentSpotlightItems.Count; i++)
        {
            var item = _currentSpotlightItems[i];
            bool isSelected = i == _selectedSpotlightIndex;

            string actionHint = item.Kind switch
            {
                SpotlightResultKind.MathResult => "Salin",
                SpotlightResultKind.ClipboardItem => "Salin",
                SpotlightResultKind.SystemCommand => "Jalankan",
                SpotlightResultKind.WebUrl => "Buka",
                _ => "Buka"
            };

            Geometry? vectorGeom = null;
            if (!string.IsNullOrEmpty(item.VectorIconKey))
            {
                vectorGeom = GetVectorGeometry(item.VectorIconKey);
            }

            viewModels.Add(new SpotlightItemViewModel
            {
                Id = item.Id,
                Title = item.Title,
                Subtitle = item.Subtitle,
                ActionHintText = actionHint,
                TitleBrush = isSelected ? accentBrush : (Brush)BrushCache.Get(Color.FromRgb(248, 250, 252)),
                ItemBackgroundBrush = isSelected
                    ? BrushCache.Get(Color.FromArgb(45, 56, 189, 248))
                    : BrushCache.Get(Color.FromArgb(16, 255, 255, 255)),
                ItemBorderBrush = isSelected
                    ? BrushCache.Get(Color.FromArgb(90, 56, 189, 248))
                    : BrushCache.Get(Color.FromArgb(20, 255, 255, 255)),
                ResolvedVectorIcon = vectorGeom,
                VectorIconVisibility = vectorGeom != null ? Visibility.Visible : Visibility.Collapsed,
                IconImage = item.IconImage,
                ImageIconVisibility = item.IconImage != null ? Visibility.Visible : Visibility.Collapsed,
                SourceItem = item
            });
        }

        SpotlightItemsControl.ItemsSource = viewModels;
    }

    private void ExecuteSelectedSpotlightItem()
    {
        if (_currentSpotlightItems.Count == 0) return;
        int idx = Math.Clamp(_selectedSpotlightIndex >= 0 ? _selectedSpotlightIndex : 0, 0, _currentSpotlightItems.Count - 1);
        ExecuteSpotlightItem(_currentSpotlightItems[idx]);
    }

    private void ExecuteSpotlightItem(SpotlightItem item)
    {
        try
        {
            item.ExecuteAction?.Invoke();

            if (item.Kind == SpotlightResultKind.MathResult)
            {
                TriggerToast(new ToastPayload(
                    Icon: "CALCULATOR",
                    Title: "Hasil Disalin",
                    Subtitle: item.Title,
                    AccentHex: "#22C55E",
                    Duration: TimeSpan.FromSeconds(2.0)));
            }
            else if (item.Kind == SpotlightResultKind.ClipboardItem)
            {
                TriggerToast(new ToastPayload(
                    Icon: "FOLDER",
                    Title: "Tersalin ke Clipboard",
                    Subtitle: item.Title,
                    AccentHex: "#38BDF8",
                    Duration: TimeSpan.FromSeconds(2.0)));
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError("ExecuteSpotlightItem", "Failed executing spotlight item", ex);
        }
        finally
        {
            CloseSpotlight();
        }
    }

    // =========================================================================
    // FEATURE 2: PER-APP AUDIO VOLUME MIXER
    // =========================================================================
}

public sealed class AudioDevicePickerItemViewModel
{
    public string DeviceId { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public bool IsDefault { get; init; }
    public Geometry? IconGeometry { get; init; }
    public Brush AccentBrush { get; init; } = Brushes.White;
    public Brush RowBackground { get; init; } = Brushes.Transparent;
    public Brush RowBorderBrush { get; init; } = Brushes.Transparent;
    public Visibility ActiveCheckVisibility { get; init; } = Visibility.Collapsed;
}

public sealed class ClipboardItemViewModel
{
    public string Id => SourceItem.Id;
    public string BadgeLabel => SourceItem.BadgeLabel;
    public Brush BadgeBrush { get; init; } = Brushes.SlateGray;
    public string PreviewText => SourceItem.PreviewText;
    public BitmapSource? ThumbnailImage => SourceItem.ThumbnailImage;
    public Visibility TextVisibility => SourceItem.TextVisibility;
    public Visibility ImageVisibility => SourceItem.ImageVisibility;
    public string TimeAgoText => SourceItem.TimeAgoText;
    public string MetadataLabel => SourceItem.MetadataLabel;
    public ClipboardItem SourceItem { get; init; } = null!;
}

public sealed class SpotlightItemViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string ActionHintText { get; init; } = "Buka";
    public Brush TitleBrush { get; init; } = Brushes.White;
    public Brush ItemBackgroundBrush { get; init; } = Brushes.Transparent;
    public Brush ItemBorderBrush { get; init; } = Brushes.Transparent;
    public Geometry? ResolvedVectorIcon { get; init; }
    public Visibility VectorIconVisibility { get; init; } = Visibility.Collapsed;
    public ImageSource? IconImage { get; init; }
    public Visibility ImageIconVisibility { get; init; } = Visibility.Collapsed;
    public SpotlightItem SourceItem { get; init; } = null!;
}

public sealed class AppMixerItemViewModel
{
    public int ProcessId { get; init; }
    public string AppName { get; init; } = string.Empty;
    public int VolumePercent { get; init; }
    public string VolumeText => IsMuted ? "Muted" : $"{VolumePercent}%";
    public double FillWidthPx { get; init; }
    public ImageSource? IconImage { get; init; }
    public Visibility FallbackVisibility => IconImage == null ? Visibility.Visible : Visibility.Collapsed;
    public bool IsMuted { get; init; }
    public Geometry? MuteIconGeometry { get; init; }
    public Brush? MuteIconBrush { get; init; }
    public string MuteToolTip => IsMuted ? "Bunyikan audio aplikasi" : "Bisukan audio aplikasi";
}

public sealed class TodoItemViewModel
{
    public int Id { get; init; }
    public string Title { get; set; } = string.Empty;
    public bool IsDone { get; init; }
    public bool IsMonochrome { get; init; }

    public Brush CheckBgBrush => IsDone
        ? (IsMonochrome ? BrushCache.Get(Color.FromArgb(46, 228, 228, 231)) : BrushCache.Get(Color.FromArgb(50, 34, 197, 94)))
        : BrushCache.Get(Color.FromArgb(20, 255, 255, 255));

    public Brush CheckBorderBrush => IsDone
        ? (IsMonochrome ? BrushCache.Get(Color.FromArgb(150, 228, 228, 231)) : BrushCache.Get(Color.FromArgb(160, 34, 197, 94)))
        : BrushCache.Get(Color.FromArgb(60, 255, 255, 255));

    public Brush CheckTickBrush => IsMonochrome
        ? BrushCache.Get(Color.FromRgb(250, 250, 250))
        : BrushCache.Get(Color.FromRgb(52, 211, 153));

    public Visibility DoneCheckmarkVisibility => IsDone ? Visibility.Visible : Visibility.Collapsed;

    public Brush TitleColorBrush => IsDone
        ? BrushCache.Get(Color.FromRgb(148, 163, 184))
        : BrushCache.Get(Color.FromRgb(248, 250, 252));

    public TextDecorationCollection? TitleDecorations => IsDone ? TextDecorations.Strikethrough : null;
}
