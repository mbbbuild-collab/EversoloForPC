using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;

namespace Eversolo;

public partial class WidgetWindow : Window
{
    readonly PlayerService _p = App.Player;
    readonly Settings _s = App.S;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public WidgetWindow()
    {
        InitializeComponent();
        ApplyMode();
        _p.Changed += Render;
        _tick.Tick += (_, _) => RenderProgress();
        _tick.Start();
        Loaded += (_, _) =>
        {
            if (!double.IsNaN(_s.WidgetLeft) && !double.IsNaN(_s.WidgetTop))
            {
                Left = _s.WidgetLeft;
                Top = _s.WidgetTop;
            }
            else
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - ActualWidth - 16;
                Top = wa.Bottom - ActualHeight - 16;
            }
            Render();
        };
    }

    void ApplyMode()
    {
        Full.Visibility = _s.WidgetMini ? Visibility.Collapsed : Visibility.Visible;
        Mini.Visibility = _s.WidgetMini ? Visibility.Visible : Visibility.Collapsed;
    }

    void Render()
    {
        var st = _p.State;
        var t = st?.Track;
        bool on = _p.Connected && st != null;

        Device.Text = on ? $"{App.DeviceName} · {st!.Output}" : Loc.T("state.offline", App.DeviceName);
        bool playing = _p.IsPlaying;
        Badge.Text = !on ? Loc.T("state.noconn") : playing ? Loc.T("state.playing") : Loc.T("state.paused");
        Badge.Foreground = new SolidColorBrush(!on ? Color.FromRgb(0xF0, 0x99, 0x7B) : playing ? Color.FromRgb(0x9F, 0xE1, 0xCB) : Color.FromRgb(0xB4, 0xB2, 0xA9));
        BadgeBox.Background = new SolidColorBrush(!on ? Color.FromArgb(0x33, 0x99, 0x3C, 0x1D) : playing ? Color.FromArgb(0x33, 0x08, 0x50, 0x41) : Color.FromArgb(0x33, 0x88, 0x87, 0x80));

        TitleT.Text = t?.Title is { Length: > 0 } ? t.Title : (on ? Loc.T("track.none") : "—");
        ArtistT.Text = t?.Artist ?? "";
        AlbumT.Text = t?.Album ?? "";
        FmtT.Text = t == null ? "" : Fmt.Format(t);
        VolT.Text = App.Local.Enabled && App.LocalAvailable ? "PC 🔊 " + (st?.VolumeDisplay ?? "") : st?.VolumeDisplay ?? "";
        LocalMenu.IsChecked = App.Local.Enabled;
        LocalMenu.IsEnabled = App.LocalAvailable; // streams: no file to mirror, option greyed out

        CoverBrush.ImageSource = _p.Cover;
        MiniCoverBrush.ImageSource = _p.Cover;
        CoverFallback.Visibility = _p.Cover == null ? Visibility.Visible : Visibility.Collapsed;

        var glyph = playing ? "" : "";
        PlayBtn.Content = glyph;
        MiniPlayBtn.Content = glyph;
        MiniTitle.Text = TitleT.Text;
        RenderProgress();
    }

    void RenderProgress()
    {
        var st = _p.State;
        var pos = _p.PositionNow;
        var dur = _p.DurationNow;
        double r = dur > 0 ? Math.Clamp((double)pos / dur, 0, 1) : 0;
        ProgCol.Width = new GridLength(r, GridUnitType.Star);
        RestCol.Width = new GridLength(1 - r, GridUnitType.Star);
        PosT.Text = Fmt.Time(pos);
        DurT.Text = Fmt.Time(dur);
        var artist = st?.Track?.Artist ?? "";
        MiniSub.Text = st == null ? Loc.T("state.offline.short") : (artist.Length > 0 ? artist + " · " : "") + $"{PosT.Text} / {DurT.Text}";
    }

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) return;
        try { DragMove(); } catch { return; }
        _s.WidgetLeft = Left;
        _s.WidgetTop = Top;
        _s.Save();
    }

    void OnToggleMode(object sender, RoutedEventArgs e)
    {
        _s.WidgetMini = !_s.WidgetMini;
        _s.Save();
        ApplyMode();
    }

    void OnPlayPause(object sender, RoutedEventArgs e) => _ = _p.PlayOrPause();
    void OnNext(object sender, RoutedEventArgs e) => _ = _p.Next();
    void OnPrev(object sender, RoutedEventArgs e) => _ = _p.Prev();
    void OnBig(object sender, RoutedEventArgs e) => App.Current.ToggleBig();
    void OnLocalToggle(object sender, RoutedEventArgs e) => App.Local.Enabled = LocalMenu.IsChecked;
    void OnSettings(object sender, RoutedEventArgs e) => App.Current.ShowSettings();
    void OnHide(object sender, RoutedEventArgs e) => Hide();
    void OnExit(object sender, RoutedEventArgs e) => App.Current.ExitApp();
}
