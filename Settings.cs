using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassDock;

/// <summary>User settings, stored as JSON in %AppData%\GlassDock\settings.json.</summary>
internal sealed class Settings
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlassDock");
    public static readonly string FilePath = Path.Combine(Folder, "settings.json");
    public static readonly string PinnedFolder = Path.Combine(Folder, "Pinned");

    /// <summary>Pinned apps: paths to .lnk/.exe files, or "shell:AppsFolder\&lt;AUMID&gt;" for Store apps.</summary>
    public List<string> Pinned { get; set; } = new();

    public double IconSize { get; set; } = 37;
    public double CornerRadius { get; set; } = 19;
    public double BottomMargin { get; set; } = 6;
    public double SideMargin { get; set; } = 6;
    /// <summary>0..1 strength of the colour tint over the blur. Lower = clearer glass.</summary>
    public double GlassOpacity { get; set; } = 0.43;
    /// <summary>0..1 strength of the glass's white light layers (sheen, shine, edge glow, rim). Lower = less white.</summary>
    public double GlassHighlights { get; set; } = 0.45;
    /// <summary>Frosted glass (system blur) when >= 0.5, clear glass below. The system blur can't be blended partially.</summary>
    public double GlassBlur { get; set; } = 0;
    /// <summary>Real refraction: the background behind the bars is magnified and softened towards their centre.
    /// Needs the bars to be excluded from screen capture, so they don't appear in screenshots while on.</summary>
    public bool Refraction { get; set; } = true;
    /// <summary>0..1 strength of the refraction lens.</summary>
    public double RefractionStrength { get; set; } = 0.8;
    /// <summary>0.1..1 how far in from the rim the refraction and blur build up (share of the rim-to-centre distance).</summary>
    public double RefractionEdge { get; set; } = 1;
    /// <summary>0..1 blur seen through the refracting glass; strongest at the centre, none at the rim.</summary>
    public double RefractionBlur { get; set; } = 0.35;
    /// <summary>How many times a second the glass re-reads the screen behind it (lower = lighter on the CPU/GPU).</summary>
    public double RefractionFps { get; set; } = 20;
    /// <summary>For weaker PCs: no open/close animations on the Start menu and Quick Settings, and the glass updates
    /// only 5 times a second (RefractionFps is kept for when this is turned off).</summary>
    public bool LowPowerMode { get; set; } = false;
    /// <summary>Look for a newer GlassDock on GitHub shortly after start and once a day.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>A release the user chose to skip ("v1.2.3"); it isn't offered again automatically.</summary>
    public string? SkippedVersion { get; set; }
    /// <summary>The glass update rate actually used.</summary>
    [JsonIgnore] public double EffectiveRefractionFps => LowPowerMode ? 5 : RefractionFps;
    /// <summary>Hide the bars until the cursor touches the bottom edge of the screen; they float over apps.</summary>
    public bool AutoHide { get; set; } = true;
    /// <summary>Only when AutoHide is off: keep maximised windows above the dock.</summary>
    public bool ReserveScreenSpace { get; set; } = false;
    /// <summary>Wave magnification of dock icons under the cursor (1 = off).</summary>
    public double Magnification { get; set; } = 1.35;
    public bool HideWindowsTaskbar { get; set; } = true;
    public bool ShowSeconds { get; set; } = false;
    public bool ShowDate { get; set; } = true;
    public bool ShowStartButton { get; set; } = true;
    /// <summary>Now-playing bubble next to the dock while some app is playing media.</summary>
    public bool ShowMediaPlayer { get; set; } = true;
    /// <summary>Total width (DIPs) of the media player bubble.</summary>
    public double MediaPlayerWidth { get; set; } = 200;
    public bool ShowMediaArt { get; set; } = true;
    public bool ShowMediaTitle { get; set; } = false;
    public bool ShowMediaArtist { get; set; } = false;

    /// <summary>The Start button opens GlassDock's glass Start menu instead of Windows' one.</summary>
    public bool GlassStartMenu { get; set; } = true;
    /// <summary>A lone press of the Windows key opens the glass Start menu (Windows-key shortcuts keep working).</summary>
    public bool WindowsKeyOpensGlassStart { get; set; } = true;
    /// <summary>Volume and brightness changes show GlassDock's glass indicator instead of Windows' pop-up.</summary>
    public bool GlassVolumeIndicator { get; set; } = true;
    /// <summary>Apps pinned in the glass Start menu (ids in the shell's Applications folder).</summary>
    public List<string> StartPins { get; set; } = new()
    {
        "Microsoft.Windows.Explorer",
        "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
        "Microsoft.WindowsStore_8wekyb3d8bbwe!App",
    };

    /// <summary>Internal: set once the user's existing taskbar pins were imported.</summary>
    public bool ImportedTaskbarPins { get; set; }

    public static Settings Current { get; private set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>True when no settings file existed yet (first run on this computer / user).</summary>
    public static bool IsFirstRun { get; private set; }

    public static void Load()
    {
        IsFirstRun = !File.Exists(FilePath);
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch (Exception ex) { App.Log(ex); Current = new Settings(); }
        Current.StartPins ??= new();
        Current.IconSize = Math.Clamp(Current.IconSize, 24, 96);
        Current.Magnification = Math.Clamp(Current.Magnification, 1, 2.5);
        Current.MediaPlayerWidth = Math.Clamp(Current.MediaPlayerWidth, 200, 600);
        Current.GlassBlur = Math.Clamp(Current.GlassBlur, 0, 1);
        Current.GlassHighlights = Math.Clamp(Current.GlassHighlights, 0, 1);
        Current.RefractionStrength = Math.Clamp(Current.RefractionStrength, 0, 1);
        Current.RefractionBlur = Math.Clamp(Current.RefractionBlur, 0, 1);
        Current.RefractionFps = Math.Clamp(Math.Round(Current.RefractionFps), 5, 60);
        Current.RefractionEdge = Math.Clamp(Current.RefractionEdge, 0.1, 1);
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception ex) { App.Log(ex); }
    }
}
