using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GlassDock;

/// <summary>"A new version is available": what's new, and Update (download + install), Later or Skip this version.</summary>
internal sealed class UpdateWindow : Window
{
    private static UpdateWindow? _open;

    private readonly Updater.Release _release;
    private readonly Button _update, _later, _skip;
    private readonly ProgressBar _progress = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 6, 0, 0), FontSize = 12, Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _download;

    public static void ShowFor(Updater.Release release)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new UpdateWindow(release);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private UpdateWindow(Updater.Release release)
    {
        _release = release;
        Title = "GlassDock update";
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/GlassDock;component/assets/GlassDock.ico"));
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;
        FontFamily = (FontFamily)Application.Current.Resources["UiFont"];
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "MenuBg");
        SetResourceReference(ForegroundProperty, "Fg");

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18) };
        root.Children.Add(new TextBlock { Text = $"GlassDock {release.Version} is available", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var current = new TextBlock { Text = $"You have version {Updater.Current}.", Margin = new Thickness(0, 4, 0, 0) };
        current.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        root.Children.Add(current);

        string notes = CleanNotes(release.Notes);
        if (notes.Length > 0)
        {
            var header = new TextBlock { Text = "What's new", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) };
            root.Children.Add(header);
            var box = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10) };
            box.SetResourceReference(Border.BackgroundProperty, "HoverFill");
            box.Child = new ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap, LineHeight = 19 },
            };
            root.Children.Add(box);
        }

        root.Children.Add(_progress);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        root.Children.Add(_status);

        bool canInstall = Updater.IsInstalled && release.SetupUrl != null;
        _skip = Button("Skip this version", Skip);
        _later = Button("Later", Close);
        _update = Button(canInstall ? "Update" : "Open download page", canInstall ? StartUpdate : OpenPage);
        _update.IsDefault = true;
        _update.FontWeight = FontWeights.SemiBold;
        var buttons = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_skip, Dock.Left);
        _skip.Margin = new Thickness(0);
        buttons.Children.Add(_skip);
        DockPanel.SetDock(_update, Dock.Right);
        DockPanel.SetDock(_later, Dock.Right);
        buttons.Children.Add(_update);
        buttons.Children.Add(_later);
        root.Children.Add(buttons);

        if (!canInstall)
        {
            var hint = new TextBlock
            {
                Text = "This copy of GlassDock isn't the installed one, so it can't update itself. Download the new version from GitHub.",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 12, 0, 0),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
            root.Children.Add(hint);
        }

        Content = root;
        Closing += (_, e) =>
        {
            if (_download == null) return;
            if (MessageBox.Show(this, "Stop downloading the update?", "GlassDock", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                e.Cancel = true;
            else _download?.Cancel();
        };
    }

    private async void StartUpdate()
    {
        _update.IsEnabled = _skip.IsEnabled = false;
        _later.Content = "Cancel";
        _progress.Visibility = _status.Visibility = Visibility.Visible;
        _status.Text = "Downloading…";
        _download = new CancellationTokenSource();
        var progress = new Progress<double>(p =>
        {
            _progress.Value = p;
            _status.Text = $"Downloading… {p:P0}";
        });
        try
        {
            string setup = await Updater.DownloadAsync(_release, progress, _download.Token);
            _download = null;
            _status.Text = "Installing… GlassDock will restart in a moment.";
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render); // show that first
            Updater.Install(setup);
        }
        catch (OperationCanceledException) { ResetAfterDownload(""); }
        catch (Exception ex)
        {
            App.Log(ex);
            ResetAfterDownload("The update couldn't be downloaded: " + ex.Message);
        }
    }

    private void ResetAfterDownload(string message)
    {
        _download = null;
        _update.IsEnabled = _skip.IsEnabled = true;
        _later.Content = "Later";
        _progress.Visibility = Visibility.Collapsed;
        _status.Text = message;
        _status.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Skip()
    {
        Settings.Current.SkippedVersion = _release.Tag;
        Settings.Save();
        Close();
    }

    private void OpenPage()
    {
        Updater.OpenReleasePage(_release.PageUrl);
        Close();
    }

    /// <summary>The release notes as plain text: the changes only, without the install instructions and Markdown marks.</summary>
    private static string CleanNotes(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n')
            .TakeWhile(l => !l.TrimStart().StartsWith("## Install", StringComparison.OrdinalIgnoreCase))
            .Where(l => !Regex.IsMatch(l.Trim(), @"^#{1,6}\s*(Changes|Fixes|New)\s*$", RegexOptions.IgnoreCase))
            .Select(l => Regex.Replace(l, @"^#{1,6}\s*", ""))
            .Select(l => Regex.Replace(l, @"^(\s*)[-*]\s+", "$1• "))
            .Select(l => l.Replace("**", "").Replace("`", ""))
            .Select(l => Regex.Replace(l, @"\[([^\]]+)\]\([^)]+\)", "$1"));
        string text = Regex.Replace(string.Join("\n", lines), @"\n+(?=• )", "\n\n"); // a gap between points
        return Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
    }

    private static Button Button(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }
}
