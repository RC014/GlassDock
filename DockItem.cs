using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ManagedShell.WindowsTasks;

namespace GlassDock;

/// <summary>One icon in the dock: a pinned app, a running app, or both.</summary>
internal sealed class DockItem : INotifyPropertyChanged
{
    public DockItem(string key) { Key = key; }

    /// <summary>Grouping key: "aumid:&lt;id&gt;" or "exe:&lt;path&gt;".</summary>
    public string Key { get; set; }

    /// <summary>For pinned items: the settings entry (lnk/exe/shell: path).</summary>
    public string? PinPath { get; set; }
    public string? ExePath { get; set; }
    public string? AppUserModelId { get; set; }

    /// <summary>For pinned launcher shortcuts: .exe file names in the shortcut's arguments ("--processStart App.exe").</summary>
    public HashSet<string> LaunchHints { get; } = new(System.StringComparer.OrdinalIgnoreCase);

    public List<ApplicationWindow> Windows { get; } = new();

    private int _windowCount;
    /// <summary>Open windows, capped at 4: one indicator dot each.</summary>
    public int WindowCount { get => _windowCount; set => Set(ref _windowCount, value); }

    private string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    private ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }

    private bool _isPinned;
    public bool IsPinned { get => _isPinned; set => Set(ref _isPinned, value); }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }

    private bool _isActive;
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    private bool _isLaunching;
    public bool IsLaunching { get => _isLaunching; set => Set(ref _isLaunching, value); }

    private bool _isFlashing;
    public bool IsFlashing { get => _isFlashing; set => Set(ref _isFlashing, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
