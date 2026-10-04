using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using GlassDock.Interop;
using ManagedShell.WindowsTasks;

namespace GlassDock;

public partial class DockWindow : GlassWindow
{
    private readonly ObservableCollection<DockItem> _pinned = new();
    private readonly ObservableCollection<DockItem> _running = new();
    private readonly Dictionary<IntPtr, long> _lastActivated = new();
    private readonly HashSet<ApplicationWindow> _watched = new();
    private AppBar? _appBar;
    private bool _rebuildQueued;

    // drag-to-reorder state
    private DockItem? _pressItem;
    private Point _pressPoint;
    private bool _dragging;

    public DockWindow()
    {
        Resources["IconSize"] = Settings.Current.IconSize;
        Resources["Radius"] = new CornerRadius(Settings.Current.CornerRadius);
        InitializeComponent();

        StartCell.Visibility = Settings.Current.ShowStartButton ? Visibility.Visible : Visibility.Collapsed;
        PinnedList.ItemsSource = _pinned;
        RunningList.ItemsSource = _running;

        // Layout: transparent headroom above the bar for magnified icons, and side room for the bar to stretch.
        double s = Settings.Current.IconSize, m = Settings.Current.Magnification;
        _sidePad = m > 1 ? Math.Ceiling(s * (m - 1) * MagnifyRange) + 4 : 0;
        HeadRow.Height = new GridLength(Math.Ceiling(s * (m - 1)) + 40);
        BarRow.Height = new GridLength(BarHeight);
        IconStrip.Margin = new Thickness(_sidePad + 9, 8, _sidePad + 9, 2);
        BarBg.Margin = new Thickness(_sidePad, 0, _sidePad, 0);
        // keep the media bubble anchored to the dock's right edge
        SizeChanged += (_, _) => App.Media?.RelayoutNow();
        LocationChanged += (_, _) => App.Media?.RelayoutNow();
        _previewTimer.Tick += (_, _) => PreviewTick();
        MouseEnter += (_, _) => { StartMagnifier(); StartPreviewTracking(); };
        MouseMove += (_, _) => StartMagnifier();

        foreach (var path in Settings.Current.Pinned) _pinned.Add(Pins.CreatePinned(path));

        var tasks = App.Shell.TasksService;
        ((INotifyCollectionChanged)AllWindowsSource).CollectionChanged += Windows_CollectionChanged;
        tasks.WindowActivated += (_, e) => { _lastActivated[e.Window.Handle] = Stopwatch.GetTimestamp(); QueueRebuild(); };

        PreviewMouseMove += Dock_PreviewMouseMove;
        PreviewMouseLeftButtonUp += Dock_PreviewMouseLeftButtonUp;
        Drop += Dock_Drop;
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };

        SourceInitialized += (_, _) =>
        {
            if (Settings.Current.AutoHide || !Settings.Current.ReserveScreenSpace) return;
            _appBar = new AppBar(Handle);
            _appBar.FullScreenChanged += App.RaiseFullScreen;
            ReserveScreenSpace();
        };
        Rebuild();
    }

    // ---------------- model ----------------

    /// <summary>ManagedShell's full window list (the source behind its grouped taskbar view).</summary>
    private static System.Collections.IEnumerable AllWindowsSource => App.Shell.Tasks.GroupedWindows.SourceCollection;

    private void Windows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRebuild();

    private void Window_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ApplicationWindow.State) or nameof(ApplicationWindow.ShowInTaskbar)
            or nameof(ApplicationWindow.Icon) or nameof(ApplicationWindow.Title))
            QueueRebuild();
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() => { _rebuildQueued = false; Rebuild(); }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        var all = AllWindowsSource.Cast<ApplicationWindow>().ToList();

        // keep PropertyChanged subscriptions in sync
        foreach (var w in all.Where(w => _watched.Add(w))) w.PropertyChanged += Window_PropertyChanged;
        foreach (var w in _watched.Where(w => !all.Contains(w)).ToList()) { w.PropertyChanged -= Window_PropertyChanged; _watched.Remove(w); }

        var visible = all.Where(w => w.ShowInTaskbar).ToList();
        foreach (var item in _pinned.Concat(_running)) item.Windows.Clear();

        var groups = new Dictionary<string, DockItem>();
        var order = new List<DockItem>();
        foreach (var w in visible)
        {
            var pin = _pinned.FirstOrDefault(p => Pins.Matches(p, w));
            if (pin != null) { pin.Windows.Add(w); continue; }

            string key = Pins.KeyFor(w);
            if (!groups.TryGetValue(key, out var item))
            {
                item = _running.FirstOrDefault(r => r.Key == key) ?? Pins.CreateRunning(w);
                groups[key] = item;
                order.Add(item);
            }
            item.Windows.Add(w);
        }

        for (int i = _running.Count - 1; i >= 0; i--)
            if (!groups.ContainsKey(_running[i].Key)) _running.RemoveAt(i);
        foreach (var item in order)
            if (!_running.Contains(item)) _running.Add(item);

        foreach (var item in _pinned.Concat(_running))
        {
            item.Windows.Sort((a, b) => LastActive(b).CompareTo(LastActive(a)));
            item.IsRunning = item.Windows.Count > 0;
            item.IsActive = item.Windows.Any(w => w.State == ApplicationWindow.WindowState.Active);
            item.IsFlashing = item.Windows.Any(w => w.State == ApplicationWindow.WindowState.Flashing);
            if (item.IsRunning) item.IsLaunching = false;
        }

        Divider.Visibility = _running.Count > 0 && (_pinned.Count > 0 || StartCell.IsVisible) ? Visibility.Visible : Visibility.Collapsed;
    }

    private long LastActive(ApplicationWindow w) => _lastActivated.TryGetValue(w.Handle, out var t) ? t : 0;

    // ---------------- clicks ----------------

    private static DockItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as DockItem;

    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressItem = ItemOf(sender);
        _pressPoint = e.GetPosition(this);
        _dragging = false;
    }

    private void Dock_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = _pressItem;
        _pressItem = null;
        if (_dragging && item != null)
        {
            EndDrag(item, e.GetPosition(this));
            return;
        }
        if (item != null && ItemUnderMouse() == item) Activate(item);
    }

    private void Item_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ItemOf(sender) is { } item) LaunchWithBounce(item);
    }

    private DockItem? ItemUnderMouse() =>
        (Mouse.DirectlyOver as DependencyObject) is { } d ? FindItem(d) : null;

    private static DockItem? FindItem(DependencyObject d)
    {
        while (d != null)
        {
            if (d is FrameworkElement { DataContext: DockItem item }) return item;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void Activate(DockItem item)
    {
        HideLabel();
        HidePreview();
        var wins = item.Windows;
        if (wins.Count == 0) { LaunchWithBounce(item); return; }

        var active = wins.FirstOrDefault(w => w.State == ApplicationWindow.WindowState.Active);
        if (active == null) { wins[0].BringToFront(); return; }

        if (wins.Count == 1)
        {
            if (active.CanMinimize) active.Minimize();
        }
        else
        {
            // several windows: cycle through them, oldest-activated next
            wins[^1].BringToFront();
        }
    }

    private async void LaunchWithBounce(DockItem item)
    {
        Pins.Launch(item);
        item.IsLaunching = true;
        await Task.Delay(1400);
        item.IsLaunching = false;
    }

    private void Start_Click(object sender, MouseButtonEventArgs e)
    {
        HideLabel();
        if (Settings.Current.GlassStartMenu) App.ToggleStartMenu();
        else Native.SendWinCombo();
    }

    /// <summary>Right-clicking the Start button opens the GlassDock menu (Task Manager, settings, quit...).</summary>
    private void Start_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HideLabel();
        OpenMenu(BuildBarMenu(), (UIElement)sender);
    }

    // ---------------- drag to move icons / drop to pin ----------------
    //  - a ghost of the icon follows the cursor; its slot stays open as a gap and the other icons slide aside
    //  - pinned icons reorder among the pinned apps; dropped well above the dock, they're removed
    //  - running (unpinned) icons reorder among the running apps; dropped on the pinned side (a marker shows
    //    where), they're pinned there

    private const double FlipSeconds = 0.18;
    private Image? _ghost;
    private Rectangle? _insertMarker;

    /// <summary>True while an icon is being dragged (auto-hide keeps the dock up).</summary>
    internal bool IsDragging => _dragging;

    private Canvas Overlay => (Canvas)LabelBox.Parent;

    private void Dock_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(pos.X - _pressPoint.X) < 6 && Math.Abs(pos.Y - _pressPoint.Y) < 6) return;
            BeginDrag(_pressItem);
        }

        bool remove = _pressItem.IsPinned && InRemoveZone(pos);
        MoveGhost(pos, remove);

        bool toPinned = !_pressItem.IsPinned && OnPinnedSide(pos);
        ShowInsertMarker(toPinned ? PinInsertX(pos) : null);
        if (remove) return;

        if (_pressItem.IsPinned) MoveTo(_pinned, PinnedList, _pressItem, e);
        else if (!toPinned) MoveTo(_running, RunningList, _pressItem, e);
    }

    private void BeginDrag(DockItem item)
    {
        _dragging = true;
        HideLabel();
        HidePreview();
        Mouse.Capture(this, CaptureMode.SubTree);

        double size = Settings.Current.IconSize * 1.12;
        _ghost = new Image
        {
            Source = item.Icon, Width = size, Height = size, Opacity = 0.92, IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(),
        };
        RenderOptions.SetBitmapScalingMode(_ghost, BitmapScalingMode.HighQuality);
        Overlay.Children.Add(_ghost);
        ContainerOf(item)?.SetCurrentValue(OpacityProperty, 0.0); // its slot stays as a gap: the drop position
    }

    private void MoveGhost(Point pos, bool remove)
    {
        if (_ghost == null) return;
        Canvas.SetLeft(_ghost, pos.X - _ghost.Width / 2);
        Canvas.SetTop(_ghost, Math.Clamp(pos.Y - _ghost.Height / 2, 0, Math.Max(0, ActualHeight - _ghost.Height)));
        _ghost.Opacity = remove ? 0.4 : 0.92;
        var st = (ScaleTransform)_ghost.RenderTransform;
        st.ScaleX = st.ScaleY = remove ? 0.8 : 1;
    }

    /// <summary>Shows a thin marker at x (window DIPs) where a running app would be pinned, or hides it.</summary>
    private void ShowInsertMarker(double? x)
    {
        if (x == null)
        {
            if (_insertMarker != null) _insertMarker.Visibility = Visibility.Collapsed;
            return;
        }
        if (_insertMarker == null)
        {
            _insertMarker = new Rectangle { Width = 3, RadiusX = 1.5, RadiusY = 1.5, IsHitTestVisible = false };
            _insertMarker.SetResourceReference(Shape.FillProperty, "Indicator");
            Overlay.Children.Add(_insertMarker);
        }
        double h = Settings.Current.IconSize;
        _insertMarker.Height = h;
        _insertMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(_insertMarker, x.Value - 1.5);
        Canvas.SetTop(_insertMarker, HeadRow.ActualHeight + 8);
    }

    /// <summary>Index among the pinned apps where a running app dropped at pos would be inserted.</summary>
    private int PinInsertIndex(Point pos)
    {
        for (int i = 0; i < _pinned.Count; i++)
            if (PinnedList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c && LayoutCenterX(c) > pos.X)
                return i;
        return _pinned.Count;
    }

    /// <summary>x (window DIPs) of the gap at <see cref="PinInsertIndex"/>.</summary>
    private double PinInsertX(Point pos)
    {
        int i = PinInsertIndex(pos);
        if (PinnedList.ItemContainerGenerator.ContainerFromIndex(Math.Min(i, _pinned.Count - 1)) is not FrameworkElement c)
            return Divider.TranslatePoint(new Point(0, 0), this).X - 4;
        double left = c.TranslatePoint(new Point(0, 0), this).X;
        return i < _pinned.Count ? left : left + c.ActualWidth;
    }

    private double LayoutCenterX(FrameworkElement c)
    {
        var offset = VisualTreeHelper.GetOffset(c); // layout position, ignoring slide animations
        var panel = (Visual)VisualTreeHelper.GetParent(c);
        return panel.TransformToAncestor(this).Transform(new Point(offset.X + c.ActualWidth / 2, 0)).X;
    }

    /// <summary>Moves the dragged item to the slot of the list the cursor is over (clamped to the list's ends),
    /// animating the other icons into their new places.</summary>
    private void MoveTo(ObservableCollection<DockItem> list, ItemsControl view, DockItem item, MouseEventArgs e)
    {
        int from = list.IndexOf(item);
        if (from < 0) return;
        double x = e.GetPosition(this).X;
        // the new index is the number of other icons whose centre is left of the cursor
        int to = 0;
        for (int i = 0; i < list.Count; i++)
            if (i != from && view.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c && LayoutCenterX(c) < x)
                to++;
        if (to == from) return;

        // FLIP: remember where every icon is drawn, reorder, then slide each from its old spot to its new one.
        var before = new Dictionary<UIElement, double>();
        for (int i = 0; i < list.Count; i++)
            if (view.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c)
                before[c] = c.TranslatePoint(new Point(0, 0), this).X;

        list.Move(from, to);
        view.UpdateLayout();

        foreach (var (c, oldX) in before)
        {
            if (c is not FrameworkElement fe) continue;
            var tt = fe.RenderTransform as TranslateTransform;
            if (tt == null || tt.IsFrozen) fe.RenderTransform = tt = new TranslateTransform();
            double layoutX = fe.TranslatePoint(new Point(0, 0), this).X - tt.X;
            double delta = oldX - layoutX;
            if (Math.Abs(delta) < 0.5) continue;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(delta, 0, TimeSpan.FromSeconds(FlipSeconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }

    /// <summary>Well above the bar: dropping a pinned icon here removes it from the dock.</summary>
    private bool InRemoveZone(Point pos) => pos.Y < HeadRow.ActualHeight - Settings.Current.IconSize * 0.6;

    /// <summary>Whether a point is left of the divider, i.e. over the pinned apps.</summary>
    private bool OnPinnedSide(Point pos) =>
        Divider.Visibility == Visibility.Visible && pos.X < Divider.TranslatePoint(new Point(0, 0), this).X;

    private void EndDrag(DockItem item, Point pos)
    {
        _dragging = false;
        Mouse.Capture(null);
        if (_ghost != null) { Overlay.Children.Remove(_ghost); _ghost = null; }
        ShowInsertMarker(null);
        ContainerOf(item)?.SetCurrentValue(OpacityProperty, 1.0);

        if (item.IsPinned)
        {
            if (InRemoveZone(pos)) Unpin(item);
            else SavePinOrder();
        }
        else if (OnPinnedSide(pos) && Pins.PinPathFor(item) is { } path)
        {
            Pin(path, PinInsertIndex(pos));
        }
    }

    private UIElement? ContainerOf(DockItem item) =>
        PinnedList.ItemContainerGenerator.ContainerFromItem(item) as UIElement
        ?? RunningList.ItemContainerGenerator.ContainerFromItem(item) as UIElement;

    private void SavePinOrder()
    {
        Settings.Current.Pinned = _pinned.Select(p => p.PinPath!).ToList();
        Settings.Save();
    }

    private void Dock_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)))
            Pin(f);
    }

    /// <summary>Adds an app to the dock (the Start menu's "Keep in Dock").</summary>
    internal void KeepInDock(string path) => Pin(path);

    internal bool IsInDock(string path) => _pinned.Any(p => string.Equals(p.PinPath, path, StringComparison.OrdinalIgnoreCase));

    private void Pin(string path, int index = -1)
    {
        if (_pinned.Any(p => string.Equals(p.PinPath, path, StringComparison.OrdinalIgnoreCase))) return;
        var item = Pins.CreatePinned(path);
        if (index < 0 || index > _pinned.Count) _pinned.Add(item);
        else _pinned.Insert(index, item);
        SavePinOrder();
        Rebuild();
    }

    private void Unpin(DockItem item)
    {
        _pinned.Remove(item);
        SavePinOrder();
        Rebuild();
    }

    // ---------------- window previews ----------------
    // Hovering a running app's icon for a moment shows live previews of its windows above the dock; moving to
    // another running app switches them at once; leaving both the icon and the previews hides them.

    private static readonly TimeSpan PreviewDwell = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan PreviewLinger = TimeSpan.FromMilliseconds(300);
    private readonly PreviewWindow _preview = new();
    private readonly System.Windows.Threading.DispatcherTimer _previewTimer =
        new(System.Windows.Threading.DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(50) };
    private DockItem? _previewHover;
    private DateTime _previewHoverSince, _previewLeftAt = DateTime.MaxValue;

    /// <summary>The preview panel (for the GLASSDOCK_DUMP debug renders).</summary>
    internal Window PreviewPanel => _preview;

    /// <summary>True while previews are open (auto-hide keeps the dock up).</summary>
    internal bool IsPreviewOpen => _preview.IsVisible;

    private void StartPreviewTracking()
    {
        if (!_previewTimer.IsEnabled) _previewTimer.Start();
    }

    private void HidePreview()
    {
        _previewHover = null;
        if (_preview.IsVisible) _preview.HideNow();
    }

    private void PreviewTick()
    {
        Native.GetCursorPos(out var p);
        var now = DateTime.UtcNow;
        DockItem? over = IsVisible && IsSlidIn && !_dragging && !HasOpenMenu ? RunningItemAt(p) : null;

        if (over != null)
        {
            _previewLeftAt = DateTime.MaxValue;
            if (_preview.IsVisible)
            {
                if (_preview.Item != over) ShowPreview(over);
            }
            else if (_previewHover != over) (_previewHover, _previewHoverSince) = (over, now);
            else if (now - _previewHoverSince >= PreviewDwell) ShowPreview(over);
            return;
        }

        _previewHover = null;
        if (_preview.IsVisible && IsSlidIn && _preview.ContainsScreenPoint(p)) { _previewLeftAt = DateTime.MaxValue; return; }
        if (_preview.IsVisible)
        {
            if (_previewLeftAt == DateTime.MaxValue) _previewLeftAt = now;
            if (now - _previewLeftAt >= PreviewLinger || !IsSlidIn) HidePreview();
            return;
        }
        if (!ContainsCursor(p)) _previewTimer.Stop(); // nothing to track until the cursor comes back
    }

    /// <summary>The running dock item whose icon is under a physical screen point, if any.</summary>
    private DockItem? RunningItemAt(Native.POINT p)
    {
        foreach (var slot in Slots())
        {
            if (slot.Item is not { IsRunning: true } item || slot.Cell is not FrameworkElement cell) continue;
            var tl = cell.PointToScreen(new Point(0, 0));
            var br = cell.PointToScreen(new Point(cell.ActualWidth, cell.ActualHeight));
            // the icon may be magnified upward: extend the hit area up by the magnified height
            double rise = Settings.Current.IconSize * (Math.Max(1, Settings.Current.Magnification) - 1) * VisualTreeHelper.GetDpi(this).DpiScaleY;
            if (p.X >= tl.X && p.X < br.X && p.Y >= tl.Y - rise && p.Y < br.Y) return item;
        }
        return null;
    }

    private void ShowPreview(DockItem item)
    {
        if (ContainerOf(item) is not FrameworkElement cell) return;
        HideLabel();
        // centred over the icon, above the tallest a magnified icon can get
        var centre = cell.TranslatePoint(new Point(cell.ActualWidth / 2, 0), this);
        double glassTop = Top + HeadRow.ActualHeight;
        double rise = Settings.Current.IconSize * (Math.Max(1, Settings.Current.Magnification) - 1);
        _preview.ShowFor(item, Left + centre.X, glassTop - rise - 10);
    }

    // ---------------- wave magnification & name label ----------------

    /// <summary>How many icon widths the magnification wave spreads over on each side.</summary>
    private const double MagnifyRange = 3.0;
    private double _sidePad;
    private double _intensity;
    private bool _magnifying;
    private double _extL, _extR;

    private readonly record struct Slot(FrameworkElement Cell, FrameworkElement? Host, FrameworkElement? Dot, string Name, DockItem? Item = null);

    private List<Slot> Slots()
    {
        var list = new List<Slot>();
        if (StartCell.Visibility == Visibility.Visible) list.Add(new Slot(StartCell, StartHost, null, "Start"));
        AddSlots(PinnedList, list);
        if (Divider.Visibility == Visibility.Visible) list.Add(new Slot(Divider, null, Divider, ""));
        AddSlots(RunningList, list);
        return list;
    }

    private static void AddSlots(ItemsControl items, List<Slot> slots)
    {
        for (int i = 0; i < items.Items.Count; i++)
        {
            if (items.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp || cp.ContentTemplate == null) continue;
            try
            {
                if (cp.ContentTemplate.FindName("IconHost", cp) is not FrameworkElement host) continue;
                var dot = cp.ContentTemplate.FindName("Dot", cp) as FrameworkElement;
                slots.Add(new Slot(cp, host, dot, (cp.Content as DockItem)?.Name ?? "", cp.Content as DockItem));
            }
            catch (InvalidOperationException) { /* template not applied yet */ }
        }
    }

    private void StartMagnifier()
    {
        if (_magnifying) return;
        _magnifying = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double S = Settings.Current.IconSize, M = Math.Max(1, Settings.Current.Magnification), C = S + 6;
        Native.GetCursorPos(out var p);
        var screenPt = new Point(p.X, p.Y);
        var win = PointFromScreen(screenPt);
        double mouseX = IconStrip.PointFromScreen(screenPt).X;
        double barTop = HeadRow.ActualHeight;

        bool inside = IsVisible && IsSlidIn && !_dragging &&
                      win.Y >= barTop - S * (M - 1) * _intensity - 2 && win.Y <= ActualHeight &&
                      win.X >= BarBg.Margin.Left && win.X <= ActualWidth - BarBg.Margin.Right;
        double target = inside ? 1 : 0;
        _intensity += (target - _intensity) * 0.3;
        if (Math.Abs(target - _intensity) < 0.003) _intensity = target;
        // Hiding: flatten at once rather than easing out during the slide (the bar shouldn't change while it moves).
        if (!IsSlidIn) _intensity = 0;

        var slots = Slots();
        int n = slots.Count;
        var center = new double[n];
        var scale = new double[n];
        var ext = new double[n];
        double total = 0, before = 0;
        for (int i = 0; i < n; i++)
        {
            var cell = slots[i].Cell;
            center[i] = cell.TranslatePoint(new Point(cell.ActualWidth / 2, 0), IconStrip).X;
            scale[i] = 1;
            if (slots[i].Host == null) continue;
            double d = (center[i] - mouseX) / C;
            double f = Math.Abs(d) < MagnifyRange ? (1 + Math.Cos(Math.PI * d / MagnifyRange)) / 2 : 0;
            scale[i] = 1 + (M - 1) * f * _intensity;
            ext[i] = S * (scale[i] - 1);
            total += ext[i];
            // how much of this icon's growth lies left of the cursor: keeps the icon under the cursor in place
            before += ext[i] * Math.Clamp((mouseX - (center[i] - C / 2)) / C, 0, 1);
        }

        double cum = 0;
        int hovered = -1;
        for (int i = 0; i < n; i++)
        {
            double tx = cum + ext[i] / 2 - before;
            cum += ext[i];
            if (slots[i].Host is { } host)
            {
                var tg = MutableGroup(host);
                var sc = (ScaleTransform)tg.Children[0];
                sc.ScaleX = sc.ScaleY = scale[i];
                ((TranslateTransform)tg.Children[1]).X = tx;
                if (inside && Math.Abs(center[i] - mouseX) <= C / 2) hovered = i;
            }
            if (slots[i].Dot is { } dot) MutableTranslate(dot).X = tx;
        }

        // stretch the glass bar with the icons
        if (Math.Abs(before - _extL) > 0.1 || Math.Abs(total - before - _extR) > 0.1)
        {
            _extL = before;
            _extR = total - before;
            BarBg.Margin = new Thickness(_sidePad - _extL, 0, _sidePad - _extR, 0);
            SyncBackdrop();
            App.Media?.ShiftX(_extR); // the media bubble rides along with the dock's right edge
        }

        if (hovered >= 0 && _intensity > 0.5 && !_dragging && !HasOpenMenu && slots[hovered].Host is { } h)
        {
            LabelText.Text = slots[hovered].Name;
            LabelBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = LabelBox.DesiredSize;
            var tl = h.TranslatePoint(new Point(0, 0), RootGrid);
            var br = h.TranslatePoint(new Point(h.ActualWidth, h.ActualHeight), RootGrid);
            Canvas.SetLeft(LabelBox, Math.Clamp((tl.X + br.X) / 2 - size.Width / 2, 0, Math.Max(0, ActualWidth - size.Width)));
            Canvas.SetTop(LabelBox, Math.Max(0, tl.Y - size.Height - 6));
            LabelBox.Opacity = 1;
        }
        else LabelBox.Opacity = 0;

        if (!inside && _intensity == 0)
        {
            CompositionTarget.Rendering -= OnFrame;
            _magnifying = false;
        }
    }

    // Transforms created by templates are frozen; give each element its own editable copy.
    private static TransformGroup MutableGroup(FrameworkElement e)
    {
        if (e.RenderTransform is TransformGroup { IsFrozen: false } tg) return tg;
        var g = new TransformGroup();
        g.Children.Add(new ScaleTransform());
        g.Children.Add(new TranslateTransform());
        e.RenderTransform = g;
        return g;
    }

    private static TranslateTransform MutableTranslate(FrameworkElement e)
    {
        if (e.RenderTransform is TranslateTransform { IsFrozen: false } t) return t;
        var n = new TranslateTransform();
        e.RenderTransform = n;
        return n;
    }

    private void HideLabel() => LabelBox.Opacity = 0;

    // ---------------- menus ----------------

    private void Item_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        e.Handled = true;
        HideLabel();
        HidePreview();

        var menu = new ContextMenu();
        foreach (var w in item.Windows)
        {
            var win = w;
            string title = string.IsNullOrWhiteSpace(win.Title) ? item.Name : win.Title;
            if (title.Length > 60) title = title[..57] + "…";
            menu.Items.Add(MenuItem(title, () => win.BringToFront()));
        }
        if (item.Windows.Count > 0) menu.Items.Add(new Separator());

        menu.Items.Add(MenuItem(item.IsRunning ? "New window" : "Open", () => LaunchWithBounce(item)));
        if (item.IsPinned)
            menu.Items.Add(MenuItem("Remove from Dock", () => Unpin(item)));
        else if (Pins.PinPathFor(item) is { } path)
            menu.Items.Add(MenuItem("Keep in Dock", () => Pin(path)));

        string? folder = item.ExePath ?? (item.PinPath != null && File.Exists(item.PinPath) ? item.PinPath : null);
        if (folder != null)
            menu.Items.Add(MenuItem("Show in File Explorer", () => Process.Start("explorer.exe", $"/select,\"{folder}\"")));

        if (item.IsRunning)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem(item.Windows.Count > 1 ? $"Close {item.Windows.Count} windows" : "Close window",
                () => { foreach (var w in item.Windows.ToList()) w.Close(); }));
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("GlassDock settings…", SettingsWindow.ShowSingle));

        OpenMenu(menu, (UIElement)sender);
    }

    private void Bar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        HideLabel();
        OpenMenu(BuildBarMenu(), this);
    }

    internal static ContextMenu BuildBarMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Task Manager", () => Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })));
        menu.Items.Add(MenuItem("Taskbar settings", () => Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true })));
        menu.Items.Add(new Separator());
        var startup = MenuItem("Start with Windows", () => App.StartsWithWindows = !App.StartsWithWindows);
        startup.IsChecked = App.StartsWithWindows;
        menu.Items.Add(startup);
        menu.Items.Add(MenuItem("GlassDock settings…", SettingsWindow.ShowSingle));
        menu.Items.Add(MenuItem("Reload GlassDock", App.Restart));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Quit GlassDock", App.Quit));
        return menu;
    }

    internal static MenuItem MenuItem(string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        return mi;
    }

    internal static void OpenMenu(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        // Tie the menu to the bar it came from, so only that bar stays up while it's open.
        var bar = Window.GetWindow(target) as GlassWindow;
        bar?.TrackMenu(menu);
        CloseWhenCursorLeaves(menu, bar);
    }

    /// <summary>
    /// Closes the menu shortly after the cursor moves away from it. Until the cursor first enters the menu,
    /// hovering the bar it came from also keeps it open (the menu appears next to the click, not under it).
    /// </summary>
    private static void CloseWhenCursorLeaves(ContextMenu menu, GlassWindow? bar)
    {
        var grace = TimeSpan.FromMilliseconds(350);
        bool entered = false;
        DateTime? outsideSince = null;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            if (!menu.IsOpen) { timer.Stop(); return; }
            Native.GetCursorPos(out var p);
            bool inMenu = PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource src &&
                          Native.GetWindowRect(src.Handle, out var r) &&
                          p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
            entered |= inMenu;
            bool keep = inMenu || (!entered && bar != null && bar.ContainsCursor(p));
            if (keep) outsideSince = null;
            else if ((outsideSince ??= DateTime.UtcNow) + grace < DateTime.UtcNow)
            {
                menu.IsOpen = false;
                timer.Stop();
            }
        };
        menu.Closed += (_, _) => timer.Stop();
        timer.Start();
    }

    // ---------------- placement & screen space ----------------

    protected override Rect GlassRect => new(BarBg.Margin.Left, HeadRow.Height.Value,
        Math.Max(0, ActualWidth - BarBg.Margin.Left - BarBg.Margin.Right), BarRow.Height.Value);

    /// <summary>Right edge of the glass at rest (DIPs from the window's left), ignoring the magnification stretch.</summary>
    internal double RestingGlassRight => ActualWidth - _sidePad;

    /// <summary>The most the glass can stretch to either side while magnifying.</summary>
    internal double MaxStretch => _sidePad;

    /// <summary>The glass plus the magnified icons rising above it while hovered.</summary>
    protected override Rect HoverRect
    {
        get
        {
            var g = GlassRect;
            double rise = Settings.Current.IconSize * (Math.Max(1, Settings.Current.Magnification) - 1) * _intensity;
            return new Rect(g.X, g.Y - rise, g.Width, g.Height + rise);
        }
    }

    protected override void Reposition()
    {
        var s = Screen;
        Left = Math.Round((s.Width - ActualWidth) / 2);
        Top = Math.Round(s.Height - ActualHeight); // the window reaches the screen edge; the gap is a bottom margin inside it
    }

    private void ReserveScreenSpace()
    {
        if (!Settings.Current.ReserveScreenSpace || _appBar == null) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        _appBar.Reserve((int)Math.Ceiling((BarHeight + Settings.Current.BottomMargin + 6) * scale));
    }

    internal void ReleaseScreenSpace() => _appBar?.Release();

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBar != null && _appBar.HandleMessage(msg, wParam, lParam)) { handled = true; return IntPtr.Zero; }
        if (msg == WM_TASKBARCREATED && _appBar != null)
        {
            // Explorer restarted: our app bar registration is gone.
            _appBar.Reset();
            Dispatcher.BeginInvoke(ReserveScreenSpace);
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }
}