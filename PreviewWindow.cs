using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDock.Interop;
using ManagedShell.WindowsTasks;

namespace GlassDock;

/// <summary>
/// Live window previews shown above a dock icon: one card per window of the app, with a DWM thumbnail (drawn
/// and kept live by Windows), the window title and a close button. Clicking a card brings that window forward.
/// </summary>
internal sealed class PreviewWindow : Window
{
    private const double MaxThumbW = 220, MaxThumbH = 140;

    private sealed record Card(ApplicationWindow Window, Border ThumbHost, IntPtr Thumb);

    private readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal };
    private readonly List<Card> _live = new();
    private IntPtr _hwnd;
    private readonly GlassSurface? _glass;

    public DockItem? Item { get; private set; }

    public PreviewWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        SetResourceReference(BackgroundProperty, "MenuBg");

        // Same glass as the bars. Windows rounds this (non-layered) window's corners with its 8 px radius, so the
        // glass uses the same radius. The live thumbnails are drawn by Windows on top.
        Resources["Radius"] = new CornerRadius(8);
        var content = new Grid();
        if (Settings.Current.Refraction) content.Children.Add(_glass = new GlassSurface());
        content.Children.Add(new Border { Padding = new Thickness(6), Child = _cards });
        Content = content;

        SourceInitialized += (_, _) =>
        {
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW);
            int round = 2; // DWMWCP_ROUND: native Windows 11 rounded corners and shadow
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int none = Native.DWMWA_COLOR_NONE; // the glass draws its own rim
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref none, sizeof(int));
            // The glass samples the screen behind the panel, so keep the panel itself out of screen capture.
            if (Settings.Current.Refraction) Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        LayoutUpdated += (_, _) => PlaceThumbnails();
    }

    /// <summary>Shows previews for the item's windows, centred above anchorX with the bottom at bottomY (screen DIPs).</summary>
    public void ShowFor(DockItem item, double anchorX, double bottomY)
    {
        Clear();
        Item = item;
        foreach (var w in item.Windows.ToList()) _cards.Children.Add(BuildCard(w));
        if (_cards.Children.Count == 0) { HideNow(); return; }

        if (!IsVisible) Show();
        UpdateLayout();
        var screen = new Size(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        Left = Math.Round(Math.Clamp(anchorX - ActualWidth / 2, 8, screen.Width - ActualWidth - 8));
        Top = Math.Round(bottomY - ActualHeight);
        PlaceThumbnails();
        if (_glass != null) ScreenSampler.SampleNow(_glass); // background at the new position right away
    }

    public void HideNow()
    {
        Clear();
        Item = null;
        Hide();
    }

    /// <summary>Whether a physical screen point is over the preview.</summary>
    public bool ContainsScreenPoint(Native.POINT p) =>
        IsVisible && Native.GetWindowRect(_hwnd, out var r) && p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;

    private UIElement BuildCard(ApplicationWindow w)
    {
        // Register the live thumbnail first to size the card by the window's shape.
        DwmRegisterThumbnail(_hwnd, w.Handle, out IntPtr thumb);
        double tw = MaxThumbW, th = MaxThumbW * 10 / 16;
        if (thumb != IntPtr.Zero && DwmQueryThumbnailSourceSize(thumb, out var src) == 0 && src.cx > 40 && src.cy > 40)
        {
            double scale = Math.Min(MaxThumbW / src.cx, MaxThumbH / src.cy);
            tw = Math.Max(80, src.cx * scale);
            th = Math.Max(50, src.cy * scale);
        }

        // Placeholder for the thumbnail; the app icon shows through if the window has no live image (minimised).
        var thumbHost = new Border
        {
            Width = tw, Height = th, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 6, 0, 0),
            Child = new Image { Source = Item?.Icon, Width = 40, Height = 40, Opacity = 0.6 },
        };
        thumbHost.SetResourceReference(Border.BackgroundProperty, "HoverFill");

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(w.Title) ? Item?.Name : w.Title,
            FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = tw - 30,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Fg");

        var close = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Cursor = Cursors.Hand,
            ToolTip = "Close window",
            Child = new TextBlock
            {
                Text = "", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
            },
        };
        ((TextBlock)close.Child).SetResourceReference(TextBlock.ForegroundProperty, "Fg");
        close.MouseEnter += (_, _) => close.Background = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            w.Close();
            RemoveCard(w);
        };

        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(title);

        var card = new Border
        {
            Padding = new Thickness(8, 6, 8, 8), Margin = new Thickness(2), CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Child = new StackPanel { Children = { header, thumbHost } },
        };
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "HoverFill");
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        card.MouseLeftButtonUp += (_, _) =>
        {
            w.BringToFront();
            HideNow();
        };

        if (thumb != IntPtr.Zero) _live.Add(new Card(w, thumbHost, thumb));
        return card;
    }

    private void RemoveCard(ApplicationWindow w)
    {
        var card = _live.FirstOrDefault(c => c.Window == w);
        if (card != null)
        {
            DwmUnregisterThumbnail(card.Thumb);
            _live.Remove(card);
            // the card is the thumb host's StackPanel's parent Border
            if (card.ThumbHost.Parent is FrameworkElement stack && stack.Parent is UIElement cardRoot) _cards.Children.Remove(cardRoot);
        }
        if (_cards.Children.Count == 0) HideNow();
    }

    /// <summary>Keeps each live thumbnail drawn exactly over its placeholder (physical px within this window).</summary>
    private void PlaceThumbnails()
    {
        if (!IsVisible || _live.Count == 0) return;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var c in _live)
        {
            if (!c.ThumbHost.IsLoaded) continue;
            var tl = c.ThumbHost.TranslatePoint(new Point(0, 0), this);
            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new Native.RECT
                {
                    Left = (int)Math.Round(tl.X * s), Top = (int)Math.Round(tl.Y * s),
                    Right = (int)Math.Round((tl.X + c.ThumbHost.ActualWidth) * s), Bottom = (int)Math.Round((tl.Y + c.ThumbHost.ActualHeight) * s),
                },
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = false,
            };
            DwmUpdateThumbnailProperties(c.Thumb, ref props);
        }
    }

    private void Clear()
    {
        foreach (var c in _live) DwmUnregisterThumbnail(c.Thumb);
        _live.Clear();
        _cards.Children.Clear();
    }

    // ---------------- DWM thumbnails ----------------

    private const int DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public Native.RECT rcDestination;
        public Native.RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
}
