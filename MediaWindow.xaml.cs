using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ManagedShell.WindowsTasks;
using Windows.Media.Control;

namespace GlassDock;

/// <summary>
/// Now-playing bubble anchored to the right of the dock; it slides in and out together with the dock.
/// Uses Windows' system media controls, so it works with any player that reports to them.
/// </summary>
public partial class MediaWindow : GlassWindow
{
    private const double GapToDock = 10;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session; // the one shown and controlled
    private readonly System.Collections.Generic.List<GlobalSystemMediaTransportControlsSession> _watched = new();
    private int _refreshVersion;
    private bool _hasMedia;
    private readonly double _shiftRoom;

    public MediaWindow()
    {
        Resources["Radius"] = new CornerRadius(Settings.Current.CornerRadius);
        InitializeComponent();
        RootGrid.Height = BarHeight;
        ApplyDisplayOptions();
        // room on the right to ride along when the dock stretches under the hover wave
        _shiftRoom = App.Dock?.MaxStretch ?? 0;
        RootGrid.Margin = new Thickness(0, 0, _shiftRoom, Settings.Current.BottomMargin);
        double art = Settings.Current.IconSize;
        ArtFrame.Width = ArtFrame.Height = art;
        _ = InitAsync();
    }

    /// <summary>Shows or hides the cover, title and artist; without any text the bubble shrinks to fit.</summary>
    private void ApplyDisplayOptions()
    {
        var s = Settings.Current;
        ArtFrame.Visibility = s.ShowMediaArt ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = s.ShowMediaTitle ? Visibility.Visible : Visibility.Collapsed;
        ArtistText.Visibility = s.ShowMediaArtist ? Visibility.Visible : Visibility.Collapsed;
        bool text = s.ShowMediaTitle || s.ShowMediaArtist;
        TextPanel.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        InfoPanel.Visibility = text || s.ShowMediaArt ? Visibility.Visible : Visibility.Collapsed;
        RootGrid.Width = text ? s.MediaPlayerWidth : double.NaN;
    }

    /// <summary>Only shown while some app has a media session.</summary>
    internal override bool CanShow => _hasMedia;

    protected override Rect GlassRect => new(0, 0, Math.Max(0, ActualWidth - _shiftRoom), Math.Max(0, ActualHeight - Settings.Current.BottomMargin));

    protected override void Reposition()
    {
        var dock = App.Dock;
        if (dock == null) return;
        Left = Math.Round(dock.Left + dock.RestingGlassRight + GapToDock);
        Top = Math.Round(Screen.Height - ActualHeight);
    }

    // ---------------- media session ----------------

    private async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => Dispatcher.BeginInvoke(WatchSessions);
            _manager.CurrentSessionChanged += (_, _) => Dispatcher.BeginInvoke(() => _ = RefreshAsync());
            WatchSessions();
        }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>Listens to every media app, not just the one Windows calls "current".</summary>
    private void WatchSessions()
    {
        foreach (var s in _watched)
        {
            s.MediaPropertiesChanged -= Session_Changed;
            s.PlaybackInfoChanged -= Session_Changed;
        }
        _watched.Clear();
        if (_manager == null) return;
        foreach (var s in _manager.GetSessions())
        {
            s.MediaPropertiesChanged += Session_Changed;
            s.PlaybackInfoChanged += Session_Changed;
            _watched.Add(s);
        }
        _ = RefreshAsync();
    }

    private void Session_Changed(GlobalSystemMediaTransportControlsSession sender, object args) =>
        Dispatcher.BeginInvoke(() => _ = RefreshAsync());

    /// <summary>
    /// Picks what to show: something playing first, then something paused (preferring Windows' current pick).
    /// Sessions without a title (an idle player that's merely open) or stopped ones are skipped, so an open
    /// but idle app doesn't hide a track that's playing elsewhere.
    /// </summary>
    private async Task RefreshAsync()
    {
        int version = ++_refreshVersion;
        GlobalSystemMediaTransportControlsSession? best = null;
        GlobalSystemMediaTransportControlsSessionMediaProperties? bestProps = null;
        bool bestPlaying = false;
        int bestScore = 0;
        try
        {
            var current = _manager?.GetCurrentSession();
            foreach (var s in _watched.ToList())
            {
                var props = await s.TryGetMediaPropertiesAsync();
                if (version != _refreshVersion) return; // a newer refresh started meanwhile
                if (props == null || string.IsNullOrWhiteSpace(props.Title)) continue;
                var status = s.GetPlaybackInfo()?.PlaybackStatus;
                int score = status switch
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => 4,
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => 2,
                    _ => 0,
                };
                if (score == 0) continue;
                if (current != null && s.SourceAppUserModelId == current.SourceAppUserModelId) score++;
                if (score > bestScore)
                {
                    (best, bestProps, bestScore) = (s, props, score);
                    bestPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                }
            }

            _session = best;
            if (best != null && bestProps != null)
            {
                TitleText.Text = bestProps.Title;
                ArtistText.Text = string.IsNullOrWhiteSpace(bestProps.Artist) ? bestProps.AlbumTitle : bestProps.Artist;
                PlayGlyph.Text = bestPlaying ? "" : "";
                var art = await LoadThumbnailAsync(bestProps);
                if (version != _refreshVersion) return;
                Art.Source = art;
            }
        }
        catch (Exception ex) { App.Log(ex); }
        SetHasMedia(best != null);
    }

    private static async Task<BitmapImage?> LoadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        if (props.Thumbnail == null) return null;
        try
        {
            using var stream = await props.Thumbnail.OpenReadAsync();
            using var net = stream.AsStream();
            var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            ms.Position = 0;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = (int)(Settings.Current.IconSize * 3);
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    private void SetHasMedia(bool has)
    {
        if (_hasMedia == has) return;
        _hasMedia = has;
        if (!has) Hide();
        else if (App.Dock is { IsVisible: true, IsSlidIn: true }) Slide(true, 0.22); // dock is up: join it
    }

    // ---------------- buttons ----------------

    private async void PlayPause_Click(object sender, MouseButtonEventArgs e)
    {
        if (_session != null) await _session.TryTogglePlayPauseAsync();
    }

    private async void Next_Click(object sender, MouseButtonEventArgs e)
    {
        if (_session != null) await _session.TrySkipNextAsync();
    }

    private async void Previous_Click(object sender, MouseButtonEventArgs e)
    {
        if (_session != null) await _session.TrySkipPreviousAsync();
    }

    private void Bar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DockWindow.OpenMenu(DockWindow.BuildBarMenu(), this);
    }

    /// <summary>Brings the player app's window to the front.</summary>
    private void Info_Click(object sender, MouseButtonEventArgs e)
    {
        string? aumid = _session?.SourceAppUserModelId;
        if (string.IsNullOrEmpty(aumid)) return;
        var windows = App.Shell.Tasks.GroupedWindows.SourceCollection.Cast<ApplicationWindow>().Where(w => w.ShowInTaskbar).ToList();
        string exeName = aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? aumid : aumid + ".exe";
        var win = windows.FirstOrDefault(w => string.Equals(w.AppUserModelID, aumid, StringComparison.OrdinalIgnoreCase))
               ?? windows.FirstOrDefault(w => string.Equals(Path.GetFileName(w.WinFileName), exeName, StringComparison.OrdinalIgnoreCase));
        win?.BringToFront();
    }
}