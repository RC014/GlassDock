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
        MediaController.Instance.Changed += Media_Changed;
        MediaController.Instance.Start();
        Media_Changed();
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

    // ---------------- media (shared MediaController) ----------------

    private void Media_Changed()
    {
        var m = MediaController.Instance;
        if (m.HasMedia)
        {
            TitleText.Text = m.Title;
            ArtistText.Text = m.Artist;
            PlayGlyph.Text = m.IsPlaying ? "" : "";
            Art.Source = m.Art;
        }
        SetHasMedia(m.HasMedia);
    }

    private void SetHasMedia(bool has)
    {
        if (_hasMedia == has) return;
        _hasMedia = has;
        if (!has) Hide();
        else if (App.Dock is { IsVisible: true, IsSlidIn: true }) Slide(true, 0.22); // dock is up: join it
    }

    private void PlayPause_Click(object sender, MouseButtonEventArgs e) => MediaController.Instance.PlayPause();
    private void Next_Click(object sender, MouseButtonEventArgs e) => MediaController.Instance.Next();
    private void Previous_Click(object sender, MouseButtonEventArgs e) => MediaController.Instance.Previous();
    private void Info_Click(object sender, MouseButtonEventArgs e) => MediaController.Instance.ShowPlayer();

    private void Bar_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DockWindow.OpenMenu(DockWindow.BuildBarMenu(), this);
    }


}