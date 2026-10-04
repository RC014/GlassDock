# GlassDock

A glass replacement for the Windows 11 taskbar. Instead of one bar across the whole screen, you get:

- a small **dock** in the centre with your apps, which resizes itself to its contents;
- a **media player** bubble next to the dock while something is playing;
- a **status bar** in the bottom-right corner with tray icons, network, volume, battery and the clock.

All of them are drawn as glass that refracts what's behind it: the background is magnified and softened towards the middle of each bubble and stays clear at the rim.

## Download

1. Download `GlassDock.exe` from the [latest release](https://github.com/RC014/GlassDock/releases/latest). It includes everything it needs; nothing else has to be installed.
2. Put it somewhere permanent (for example `C:\Program Files\GlassDock` or a folder in your user profile) and run it.
3. Windows may show "Windows protected your PC" because the app isn't code-signed: click **More info** › **Run anyway**.

On first run GlassDock imports your current Windows taskbar pins, hides the Windows taskbar and adds itself to startup. All of this can be changed in its settings.

Requires Windows 11 (64-bit).

## Build from source

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```
git clone https://github.com/RC014/GlassDock.git
cd GlassDock
dotnet build -c Release
bin\Release\net10.0-windows10.0.22621.0\GlassDock.exe
```

To build the standalone single-file `GlassDock.exe` used for releases:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o publish
```

## Showing and hiding the bars

The bars stay hidden and float over your windows; they never take screen space away from apps.

- Push the cursor against the **bottom edge under the dock** to bring up the dock (and the media player with it).
- Push it against the **bottom edge on the right half of the screen** to bring up the status bar.
- Each bar slides away about a tenth of a second after the cursor leaves it. It stays up while one of its menus, the hidden-icons panel or a window preview is open.
- Switching to another window closes any open menus and hides the bars.
- The bars don't appear over full-screen games and videos.

## Dock

- **Click** an app to open it; click it again to minimise it. If an app has several windows, clicking cycles through them.
- **Middle-click** opens a new window of the app.
- **Hover** over the icons to magnify them in a wave and see the app's name. Hover a running app for a moment to see **live previews** of its windows: click a preview to switch to that window, or its × to close it.
- **Right-click** an app for its window list, New window, Keep in Dock / Remove from Dock, Show in File Explorer, Close and GlassDock settings.
- **Drag** any icon to move it; the other icons slide aside to show where it will land. Drag a running app onto the pinned side (left of the divider) to pin it there. Drag a pinned icon up off the dock and let go to remove it. Drop an `.exe` or `.lnk` file onto the dock to pin it.
- A dot under an icon means the app is running, a wide bar means it's the active window, and an orange dot means the app wants your attention.
- The **Windows button** on the left opens Start; right-click it for the GlassDock menu.

## Media player

- Appears next to the dock while any app is playing or paused media (music apps, videos in a browser, and so on) and slides in and out with the dock. If several apps have media, it shows the one that's playing.
- Previous / play-pause / next buttons. Click the cover or title to bring the player app to the front.
- You can choose whether it shows the cover, the title and the artist, and how wide it is.

## Status bar

- `^` shows the hidden tray icons. Icons you set to "always show" in Windows Settings › Taskbar › Other system tray icons appear directly in the bar. Clicking or right-clicking a tray icon opens that app's own menu.
- Network / volume / battery: click to open Quick Settings, scroll to change the volume, middle-click to mute.
- Click the clock to open notifications and the calendar.

## GlassDock menu

Right-click the Windows button, the media player, the clock or any empty spot on a bar (or use **GlassDock settings…** at the bottom of an app's right-click menu) for: Task Manager, Taskbar settings, Start with Windows, GlassDock settings…, Reload GlassDock and Quit GlassDock.

Menus close by themselves when you move the cursor away from them.

## Settings

**GlassDock settings…** opens a window with sliders and switches; **Save & apply** reloads GlassDock with the new settings. They are stored in `%AppData%\GlassDock\settings.json`.

| Setting | Default | Meaning |
|---|---|---|
| `IconSize` | 37 | Dock icon size |
| `Magnification` | 1.35 | How much hovered icons grow in the wave (1 = off) |
| `ShowStartButton` | true | Windows button at the left of the dock |
| `ShowMediaPlayer` | true | Media player bubble next to the dock |
| `MediaPlayerWidth` | 200 | Width of the media player (200–600) |
| `ShowMediaArt`, `ShowMediaTitle`, `ShowMediaArtist` | cover only | What the media player shows |
| `CornerRadius` | 19 | Roundness of the bubbles |
| `Refraction` | true | Real glass refraction of the background (see the note on screenshots below) |
| `RefractionStrength` | 0.8 | How strongly the glass magnifies the background (0–1) |
| `RefractionEdge` | 1 | How gradually refraction and blur build up from the rim (0.1 = thin clear rim, 1 = gradual all the way to the centre) |
| `RefractionBlur` | 0.35 | Blur seen through the glass (0–1), strongest at the centre, none at the rim |
| `GlassBlur` | 0 | When refraction is off: 1 = frosted glass (Windows blur), 0 = clear glass |
| `GlassOpacity` | 0.5 | Strength of the tint over the glass (lower = clearer) |
| `GlassHighlights` | 0.6 | Strength of the glass's white shine, edge glow and rim (lower = less white) |
| `BottomMargin` / `SideMargin` | 6 | Gap between the bubbles and the screen edges |
| `AutoHide` | true | Show the bars only when the cursor touches the bottom edge |
| `ReserveScreenSpace` | false | With `AutoHide` off: maximised windows stop above the dock |
| `HideWindowsTaskbar` | true | Hide the original Windows taskbar while GlassDock runs |
| `ShowSeconds`, `ShowDate` | false, true | Clock options |
| `Pinned` | your taskbar pins | Pinned apps: `.lnk` / `.exe` paths or `shell:AppsFolder\<AppID>` entries |

**Screenshots:** with refraction on, GlassDock reads the screen behind the bubbles, so the bubbles exclude themselves from screen capture to avoid seeing themselves. That means they don't appear in screenshots or screen recordings. Turn refraction off if you need them to.

## If the Windows taskbar doesn't come back

Quitting GlassDock restores the Windows taskbar. If GlassDock was killed or crashed, run:

```
GlassDock.exe --restore
```

## Uninstalling

1. Untick **Start with Windows** in GlassDock settings, then choose **Quit GlassDock** (this brings the Windows taskbar back).
2. Delete the GlassDock folder and `%AppData%\GlassDock` (settings and pinned shortcuts).

## Development

- The glass shaders in `Shaders\` are precompiled (`.ps`). After editing a `.fx` file, recompile it with `fxc` from the Windows SDK, e.g. `fxc /T ps_2_0 /E main /O3 /Fo Shaders\Lens.ps Shaders\Lens.fx`.
- Since the bars don't show up in screenshots while refraction is on, setting the environment variable `GLASSDOCK_DUMP=<path prefix>` makes GlassDock render each visible bar to `<prefix>_<bar>_<n>.png` once a second for a few seconds after start-up.

## Notes

- Only the primary monitor is supported.
- Built on [ManagedShell](https://github.com/cairoshell/ManagedShell), the library behind RetroBar and Cairo Desktop, for the window list and tray icons.
- MIT licensed (see `LICENSE`).
