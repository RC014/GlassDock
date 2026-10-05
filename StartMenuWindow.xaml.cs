using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GlassDock.Interop;

namespace GlassDock;

/// <summary>
/// GlassDock's Start menu, on the same glass as Quick Settings: search, pinned apps, recent files, all apps,
/// the signed-in account and power options. Opens from the dock's Start button and a lone press of the Windows
/// key; closes when it loses focus, on Esc, or after launching something.
/// </summary>
public partial class StartMenuWindow : Window
{
    private IntPtr _hwnd;
    private List<StartApp> _apps = new();
    private DateTime _hiddenAt;
    private bool _menuOpen; // a context menu of ours is open: don't close on deactivation

    public StartMenuWindow()
    {
        InitializeComponent();
        _anim = new PanelAnimation(this, Body, Glass);
        if (!Settings.Current.Refraction) Glass.Visibility = Visibility.Collapsed;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);
            int round = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int none = Native.DWMWA_COLOR_NONE;
            Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref none, sizeof(int));
            if (Settings.Current.Refraction) Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
        };

        Deactivated += (_, _) => { if (!_menuOpen) Dismiss(); };
        PreviewKeyDown += Window_PreviewKeyDown;
        _ = LoadUserAsync();
    }

    private readonly PanelAnimation _anim;

    internal void Dismiss() { if (IsVisible && !_anim.IsClosing) { _anim.Hide(); _hiddenAt = DateTime.Now; } }

    // ---------------- open / close ----------------

    /// <summary>Opens the menu, or closes it if it's open.</summary>
    internal void Toggle()
    {
        if (IsVisible && !_anim.IsClosing) { Dismiss(); return; }
        // A click on the Start button that just took focus away (and so closed the menu) shouldn't reopen it.
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 250) return;
        Open();
    }

    private void Open()
    {
        SearchBox.Text = "";
        ShowView(HomeView);
        _ = LoadAppsAsync();
        LoadRecent();

        var area = SystemParameters.WorkArea;
        Height = Math.Min(700, area.Height - 100);
        Left = Math.Round(area.Left + (area.Width - Width) / 2);
        double top = Math.Round(area.Bottom - Height - 74); // above the dock

        _anim.Show(top);
        ForceForeground(_hwnd);
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        ScreenSampler.SampleNow(Glass);
    }

    private void Launched() => Dismiss();

    /// <summary>Debug (GLASSDOCK_DUMP): opens the menu and keeps it open.</summary>
    /// <summary>
    /// Builds and draws the menu once, off-screen and without taking focus, so the first real open doesn't stall
    /// on creating it (app list, icons, first layout and render).
    /// </summary>
    internal async void Prewarm()
    {
        try
        {
            await LoadAppsAsync();
            LoadRecent();
            if (IsVisible) return;
            var area = SystemParameters.WorkArea;
            Height = Math.Min(700, area.Height - 100);
            Left = area.Left;
            Top = area.Bottom + 200; // below the screen
            ShowActivated = false;
            Show();
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); // after it rendered
            if (!IsActive) Hide();
            ShowActivated = true;
        }
        catch (Exception ex) { App.Log(ex); }
    }

    internal Window OpenForDump()
    {
        _menuOpen = true;
        if (!IsVisible) Open();
        UpdateLayout();
        return this;
    }

    // ---------------- data ----------------

    private async Task LoadAppsAsync()
    {
        try { _apps = await StartData.GetAppsAsync(); }
        catch (Exception ex) { App.Log(ex); }
        LoadPinned();
        if (AllList.ItemsSource == null || !ReferenceEquals(AllList.ItemsSource, _apps)) AllList.ItemsSource = _apps;
        if (SearchBox.Text.Length > 0) UpdateSearch();
    }

    private void LoadPinned()
    {
        var byId = _apps.GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var pinned = Settings.Current.StartPins.Select(id => byId.TryGetValue(id, out var a) ? a : null).OfType<StartApp>().ToList();
        // Same apps as last time: keep the list (rebuilding it re-creates every tile and reloads nothing new).
        if (PinnedList.ItemsSource is List<StartApp> old && old.SequenceEqual(pinned)) return;
        PinnedList.ItemsSource = pinned;
        PinnedEmpty.Visibility = pinned.Count == 0 && _apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadRecent()
    {
        var recent = StartData.GetRecent(6);
        if (RecentList.ItemsSource is List<StartRecent> old && old.Select(r => (r.LinkPath, r.When)).SequenceEqual(recent.Select(r => (r.LinkPath, r.When)))) return;
        RecentList.ItemsSource = recent;
        RecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadUserAsync()
    {
        var (name, picture) = await StartData.GetUserAsync();
        UserName.Text = name;
        if (picture != null) UserPicture.Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
    }

    // ---------------- views ----------------

    private void ShowView(FrameworkElement view)
    {
        HomeView.Visibility = view == HomeView ? Visibility.Visible : Visibility.Collapsed;
        AllView.Visibility = view == AllView ? Visibility.Visible : Visibility.Collapsed;
        SearchView.Visibility = view == SearchView ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowAllApps(object sender, MouseButtonEventArgs e)
    {
        ShowView(AllView);
        if (AllList.Items.Count > 0) AllList.ScrollIntoView(AllList.Items[0]);
    }

    private void ShowHome(object sender, MouseButtonEventArgs e) => ShowView(HomeView);

    // ---------------- search ----------------

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (SearchBox.Text.Length == 0) { ShowView(HomeView); return; }
        UpdateSearch();
    }

    private void UpdateSearch()
    {
        string q = SearchBox.Text.Trim();
        var results = _apps
            .Select(a => (App: a, Rank: Rank(a.Name, q)))
            .Where(r => r.Rank >= 0)
            .OrderBy(r => r.Rank).ThenBy(r => r.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(40).Select(r => r.App).ToList();
        ResultsList.ItemsSource = results;
        if (results.Count > 0) ResultsList.SelectedIndex = 0;
        NoResults.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowView(SearchView);
    }

    /// <summary>0 = name starts with the query, 1 = a word starts with it, 2 = contains it, -1 = no match.</summary>
    private static int Rank(string name, string q)
    {
        if (q.Length == 0) return -1;
        if (name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 0;
        int i = name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase);
        if (i < 0)
        {
            // initials, e.g. "vsc" for Visual Studio Code
            var initials = new string(name.Split(' ', '-', '.').Where(w => w.Length > 0).Select(w => w[0]).ToArray());
            return initials.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 2 : -1;
        }
        return char.IsLetterOrDigit(name[i - 1]) ? 2 : 1;
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var list = SearchView.IsVisible ? ResultsList : AllView.IsVisible ? AllList : null;
        switch (e.Key)
        {
            case Key.Down when list != null && list.Items.Count > 0:
                list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1);
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when list != null && list.Items.Count > 0:
                list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                if (list?.SelectedItem is StartApp app) { app.Launch(); Launched(); }
                e.Handled = true;
                break;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        else if (AllView.IsVisible) ShowView(HomeView);
        else Dismiss();
    }

    // ---------------- items ----------------

    private void App_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StartApp app) return;
        app.Launch();
        Launched();
    }

    private void App_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StartApp app) return;
        e.Handled = true;
        var pins = Settings.Current.StartPins;
        bool pinned = pins.Contains(app.Id, StringComparer.OrdinalIgnoreCase);
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open", () => { app.Launch(); Launched(); }));
        menu.Items.Add(Item(pinned ? "Unpin from Start" : "Pin to Start", () =>
        {
            if (pinned) pins.RemoveAll(p => string.Equals(p, app.Id, StringComparison.OrdinalIgnoreCase));
            else pins.Add(app.Id);
            Settings.Save();
            LoadPinned();
        }));
        if (pinned)
        {
            int i = pins.FindIndex(p => string.Equals(p, app.Id, StringComparison.OrdinalIgnoreCase));
            if (i > 0) menu.Items.Add(Item("Move to front", () => { pins.RemoveAt(i); pins.Insert(0, app.Id); Settings.Save(); LoadPinned(); }));
        }
        var dock = App.Dock;
        if (dock != null)
        {
            bool inDock = dock.IsInDock(app.ParsingName);
            var keep = Item("Keep in Dock", () => dock.KeepInDock(app.ParsingName));
            keep.IsEnabled = !inDock;
            if (inDock) keep.Header = "Kept in Dock";
            menu.Items.Add(keep);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Uninstall…", () => { SystemControls.OpenSettings("appsfeatures"); Launched(); }));
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private void Recent_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StartRecent r) return;
        r.Open();
        Launched();
    }

    private void Account_Click(object sender, MouseButtonEventArgs e)
    {
        SystemControls.OpenSettings("accounts");
        Launched();
    }

    private void Power_Click(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Lock", () => { Launched(); LockWorkStation(); }));
        menu.Items.Add(Item("Sign out", () => { Launched(); Run("shutdown", "/l"); }));
        menu.Items.Add(Item("Sleep", () => { Launched(); SetSuspendState(false, false, false); }));
        menu.Items.Add(Item("Shut down", () => { Launched(); Run("shutdown", "/s /t 0"); }));
        menu.Items.Add(Item("Restart", () => { Launched(); Run("shutdown", "/r /t 0"); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Windows Start menu", () => { Launched(); Native.SendWinCombo(); }));
        OpenMenu(menu, (FrameworkElement)sender);
    }

    private void OpenMenu(ContextMenu menu, FrameworkElement target)
    {
        _menuOpen = true;
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            // If focus went elsewhere while the menu was open, close now.
            Dispatcher.BeginInvoke(() => { if (IsVisible && !IsActive) Dismiss(); }, System.Windows.Threading.DispatcherPriority.Input);
        };
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    private static void Run(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false }); }
        catch (Exception ex) { App.Log(ex); }
    }

    // ---------------- focus ----------------

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr pid);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    /// <summary>
    /// Brings our window to the front even when GlassDock didn't get the last input (the Windows key is swallowed by
    /// the hook, so Windows would otherwise refuse): briefly share input state with the current foreground thread.
    /// </summary>
    private static void ForceForeground(IntPtr hwnd)
    {
        var fg = Native.GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fgThread, false);
    }
}
