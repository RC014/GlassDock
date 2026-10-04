using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GlassDock.Interop;
using ManagedShell.WindowsTasks;
using Windows.Media.Control;

namespace GlassDock;

/// <summary>
/// What's playing, from Windows' system media controls (any player that reports to them). Shared by the media
/// bubble and the Quick Settings panel. Picks something playing first, then something paused (preferring
/// Windows' current pick); idle players without a title and stopped sessions are skipped.
/// </summary>
internal sealed class MediaController
{
    public static MediaController Instance { get; } = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly List<GlobalSystemMediaTransportControlsSession> _watched = new();
    private int _refreshVersion;
    private bool _started;

    /// <summary>Raised on the UI thread whenever what's playing (or its state) changes.</summary>
    public event Action? Changed;

    public bool HasMedia => _session != null;
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public ImageSource? Art { get; private set; }
    public string AppName { get; private set; } = "";
    public ImageSource? AppIcon { get; private set; }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = InitAsync();
    }

    private static void OnUi(Action a) => Application.Current.Dispatcher.BeginInvoke(a);

    private async Task InitAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => OnUi(WatchSessions);
            _manager.CurrentSessionChanged += (_, _) => OnUi(() => _ = RefreshAsync());
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

    private void Session_Changed(GlobalSystemMediaTransportControlsSession sender, object args) => OnUi(() => _ = RefreshAsync());

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

            if (best != null && bestProps != null)
            {
                var art = await LoadThumbnailAsync(bestProps);
                if (version != _refreshVersion) return;
                if (best.SourceAppUserModelId != _session?.SourceAppUserModelId) ResolveApp(best.SourceAppUserModelId);
                Title = bestProps.Title;
                Artist = string.IsNullOrWhiteSpace(bestProps.Artist) ? bestProps.AlbumTitle : bestProps.Artist;
                IsPlaying = bestPlaying;
                Art = art;
            }
            _session = best;
        }
        catch (Exception ex) { App.Log(ex); _session = null; }
        Changed?.Invoke();
    }

    /// <summary>The player's name and icon (from its Start menu entry, or its exe).</summary>
    private void ResolveApp(string aumid)
    {
        string apps = @"shell:AppsFolder\" + aumid;
        AppName = Shell.GetDisplayName(apps) ?? Path.GetFileNameWithoutExtension(aumid);
        AppIcon = Shell.GetIcon(apps, 32);
        if (AppIcon == null && FindPlayerWindow(aumid) is { } w)
        {
            AppIcon = !string.IsNullOrEmpty(w.WinFileName) ? Shell.GetIcon(w.WinFileName, 32) : w.Icon;
            if (!string.IsNullOrWhiteSpace(w.WinFileDescription)) AppName = w.WinFileDescription;
        }
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
            img.DecodePixelWidth = 240;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    // ---------------- commands ----------------

    public async void PlayPause() { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
    public async void Next() { if (_session != null) await _session.TrySkipNextAsync(); }
    public async void Previous() { if (_session != null) await _session.TrySkipPreviousAsync(); }

    /// <summary>Brings the player app's window to the front.</summary>
    public void ShowPlayer()
    {
        if (_session?.SourceAppUserModelId is { Length: > 0 } aumid) FindPlayerWindow(aumid)?.BringToFront();
    }

    private static ApplicationWindow? FindPlayerWindow(string aumid)
    {
        var windows = App.Shell.Tasks.GroupedWindows.SourceCollection.Cast<ApplicationWindow>().Where(w => w.ShowInTaskbar).ToList();
        string exeName = aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? aumid : aumid + ".exe";
        return windows.FirstOrDefault(w => string.Equals(w.AppUserModelID, aumid, StringComparison.OrdinalIgnoreCase))
            ?? windows.FirstOrDefault(w => string.Equals(Path.GetFileName(w.WinFileName), exeName, StringComparison.OrdinalIgnoreCase));
    }
}
