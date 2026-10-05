using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace GlassDock;

/// <summary>
/// Checks GitHub for a newer GlassDock release (shortly after start and then once a day), offers it in an
/// <see cref="UpdateWindow"/>, and on "Update" downloads the release's GlassDockSetup.exe and runs it silently: the
/// installer replaces the installed copy and starts the new version.
/// </summary>
internal static class Updater
{
    private const string Repo = "RC014/GlassDock";
    private const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";
    private const string SetupAsset = "GlassDockSetup.exe";

    private static readonly HttpClient Http = CreateClient();
    private static DispatcherTimer? _daily;

    internal sealed record Release(Version Version, string Tag, string Notes, string? SetupUrl, long SetupSize, string PageUrl);

    /// <summary>This copy's version (from &lt;Version&gt; in GlassDock.csproj).</summary>
    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>True when running the installed copy (which the installer can update in place).</summary>
    public static bool IsInstalled
    {
        get
        {
            string installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GlassDock");
            return Environment.ProcessPath is { } exe
                && string.Equals(Path.GetDirectoryName(exe), installDir, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Checks a little after start-up (so it doesn't slow it down), then once a day.</summary>
    public static void StartAutomaticChecks()
    {
        DeleteOldDownloads();
        if (!Settings.Current.CheckForUpdates) return;
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        first.Tick += (_, _) => { first.Stop(); _ = CheckAsync(manual: false); };
        first.Start();
        _daily = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _daily.Tick += (_, _) => _ = CheckAsync(manual: false);
        _daily.Start();
    }

    /// <summary>
    /// Looks for a newer release and offers it. <paramref name="manual"/> ("Check for updates…"): also says when
    /// GlassDock is up to date or the check failed, and offers a version the user chose to skip.
    /// </summary>
    public static async Task CheckAsync(bool manual)
    {
        Release? latest;
        try { latest = await GetLatestAsync(); }
        catch (Exception ex)
        {
            if (!manual) return; // offline etc.: try again tomorrow, quietly
            App.Log(ex);
            MessageBox.Show("Couldn't check for updates. Check your internet connection and try again.\n\n" + ex.Message,
                "GlassDock", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (latest == null || latest.Version <= Current)
        {
            if (manual) MessageBox.Show($"GlassDock is up to date (version {Current}).", "GlassDock", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!manual && string.Equals(Settings.Current.SkippedVersion, latest.Tag, StringComparison.OrdinalIgnoreCase)) return;
        UpdateWindow.ShowFor(latest);
    }

    private static async Task<Release?> GetLatestAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        string notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
        string? url = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
                if (string.Equals(a.GetProperty("name").GetString(), SetupAsset, StringComparison.OrdinalIgnoreCase))
                {
                    url = a.GetProperty("browser_download_url").GetString();
                    size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                }
        return new Release(new Version(version.Major, version.Minor, Math.Max(0, version.Build)), tag, notes, url, size, page);
    }

    /// <summary>
    /// Downloads the release's installer to the temp folder (reporting 0..1 progress) and returns its path.
    /// </summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<double> progress, CancellationToken ct)
    {
        if (release.SetupUrl == null) throw new InvalidOperationException("This release has no installer to download.");
        string file = Path.Combine(Path.GetTempPath(), $"GlassDockSetup-{release.Tag}.exe");
        string partial = file + ".part";

        using (var response = await Http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.SetupSize;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(partial);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress.Report((double)done / total);
            }
            if (total > 0 && done != total) throw new IOException("The download was incomplete.");
        }
        File.Move(partial, file, overwrite: true);
        return file;
    }

    /// <summary>Starts the downloaded installer silently and quits; the installer puts the new version in place and starts it.</summary>
    public static void Install(string setupFile)
    {
        Process.Start(new ProcessStartInfo(setupFile, "/S") { UseShellExecute = true });
        App.Quit();
    }

    /// <summary>Installers downloaded for earlier updates aren't needed once the update ran.</summary>
    private static void DeleteOldDownloads()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "GlassDockSetup-*.exe*"))
                try { File.Delete(f); } catch { } // still running (an update in progress): leave it
        }
        catch { }
    }

    public static void OpenReleasePage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log(ex); }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"GlassDock/{Current}"); // GitHub's API requires a user agent
        return http;
    }
}
