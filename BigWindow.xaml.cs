using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace Eversolo;

/// <summary>One search-result row. Thumb/Kind/Meta/Missing are filled in asynchronously after the row is shown.</summary>
public sealed class Hit : System.ComponentModel.INotifyPropertyChanged
{
    public Hit(string kind, string title, string sub, Album? album, Track? track, bool isBack = false, string meta = "")
    {
        _kind = kind; Title = title; Sub = sub; Album = album; Track = track; IsBack = isBack; _meta = meta;
    }

    string _kind, _meta;
    bool _missing;
    BitmapImage? _thumb;

    public string Title { get; }
    public string Sub { get; }
    public Album? Album { get; }
    public Track? Track { get; }
    public bool IsBack { get; }
    public string Kind { get => _kind; set { _kind = value; Notify(nameof(Kind)); } }
    public string Meta { get => _meta; set { _meta = value; Notify(nameof(Meta)); } }
    public bool Missing { get => _missing; set { _missing = value; Notify(nameof(Missing)); } }
    public BitmapImage? Thumb { get => _thumb; set { _thumb = value; Notify(nameof(Thumb)); } }

    /// <summary>Album this row belongs to, for the thumbnail.</summary>
    public long AlbumId => Album?.Id ?? Track?.AlbumId ?? 0;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Notify(string n) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
}

public partial class BigWindow : Window
{
    readonly PlayerService _p = App.Player;
    readonly LibraryCache _lib = App.Library;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer _fx = new() { Interval = TimeSpan.FromMilliseconds(33) };
    CancellationTokenSource? _searchCts;
    Album? _openAlbum;
    BitmapImage? _shownCover;
    long _shownTrackId = -1;
    bool _frontIsA, _spinning, _spectrumVisible;
    string _queueText = "";
    readonly List<Rectangle> _bars = new();

    public BigWindow()
    {
        InitializeComponent();
        _p.Changed += Render;
        _lib.Changed += () => Dispatcher.Invoke(UpdateSearchStatus);
        _tick.Tick += (_, _) => RenderProgress();
        _fx.Tick += (_, _) => RenderSpectrum();
        Loaded += (_, _) =>
        {
            BuildGrooves();
            BuildSpectrum();
            _kenBurns = (Storyboard)FindResource("KenBurns");
            _kenBurns.Begin(this, true);
            ApplyVisualSettings();
            Render();
            _tick.Start();
            _fx.Start();
        };
    }

    Storyboard? _kenBurns;

    /// <summary>Applies the look-related settings (vinyl, background motion, spectrum) without restarting.</summary>
    public void ApplyVisualSettings()
    {
        var s = App.S;
        Disc.Visibility = s.Vinyl ? Visibility.Visible : Visibility.Collapsed;
        if (_kenBurns != null)
        {
            if (s.KenBurns) _kenBurns.Resume(this); else _kenBurns.Pause(this);
        }
        if (!s.ShowSpectrum) { SpectrumCanvas.BeginAnimation(OpacityProperty, null); SpectrumCanvas.Opacity = 0; _spectrumVisible = false; }
    }

    // ---------- window placement ----------

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    const uint SWP_SHOWWINDOW = 0x0040, SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;

    public void ShowOnScreen(int idx)
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) return;
        idx = Settings.ResolveScreen(idx);
        var b = screens[idx].Bounds; // device pixels
        WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        Activate();
        Focus();
    }

    // ---------- visuals ----------

    void BuildGrooves()
    {
        var brush = new RadialGradientBrush { GradientOrigin = new Point(0.5, 0.5), Center = new Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
        var rnd = new Random(7);
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x14, 0x14, 0x16), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0x14, 0x14, 0x16), 0.36));
        for (int i = 0; i < 70; i++)
        {
            double o = 0.36 + 0.64 * i / 70.0;
            byte v = (byte)(0x16 + (i % 2 == 0 ? 0 : 0x0A) + rnd.Next(0, 4));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(v, v, (byte)(v + 2)), o));
        }
        brush.Freeze();
        Grooves.Fill = brush;
    }

    void BuildSpectrum()
    {
        var n = App.Local.Spectrum.Bands;
        double w = SpectrumCanvas.Width / n, gap = 5;
        var fill = new LinearGradientBrush(Color.FromArgb(0xE0, 0xCE, 0xCB, 0xF6), Color.FromArgb(0x60, 0x7F, 0x77, 0xDD), 90);
        fill.Freeze();
        for (int i = 0; i < n; i++)
        {
            var r = new Rectangle { Width = w - gap, Height = 2, RadiusX = 3, RadiusY = 3, Fill = fill };
            Canvas.SetLeft(r, i * w + gap / 2);
            Canvas.SetTop(r, SpectrumCanvas.Height - 2);
            SpectrumCanvas.Children.Add(r);
            _bars.Add(r);
        }
    }

    static ImageSource Tiny(BitmapSource src)
    {
        var scale = 12.0 / Math.Max(src.PixelWidth, src.PixelHeight);
        var t = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        t.Freeze();
        return t;
    }

    static DoubleAnimation Anim(double? from, double to, int ms, IEasingFunction? ease = null)
    {
        var a = from == null ? new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) : new DoubleAnimation(from.Value, to, TimeSpan.FromMilliseconds(ms));
        a.EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut };
        return a;
    }

    void ShowCover(BitmapImage? img)
    {
        var (inRect, inBrush, outRect) = _frontIsA ? (CoverB, CoverBrushB, CoverA) : (CoverA, CoverBrushA, CoverB);
        if (img == null)
        {
            CoverA.BeginAnimation(OpacityProperty, Anim(null, 0, 500));
            CoverB.BeginAnimation(OpacityProperty, Anim(null, 0, 500));
            CoverFallback.Visibility = Visibility.Visible;
            DiscLabel.ImageSource = null;
            Bg.Source = null;
            return;
        }
        inBrush.ImageSource = img;
        inRect.BeginAnimation(OpacityProperty, Anim(null, 1, 1100));
        outRect.BeginAnimation(OpacityProperty, Anim(null, 0, 1100));
        _frontIsA = !_frontIsA;
        CoverFallback.Visibility = Visibility.Collapsed;
        DiscLabel.ImageSource = img;
        Bg.Source = Tiny(img);
    }

    void AnimateText()
    {
        // Staggered slide-in: title first, then artist, album, format.
        var items = new (TranslateTransform t, UIElement e)[] { (T1, TitleT), (T2, ArtistT), (T3, AlbumT), (T4, FmtT) };
        for (int i = 0; i < items.Length; i++)
        {
            var delay = TimeSpan.FromMilliseconds(120 * i);
            var slide = Anim(70, 0, 900); slide.BeginTime = delay;
            var fade = Anim(0, 1, 700); fade.BeginTime = delay;
            items[i].e.Opacity = 0;
            items[i].t.BeginAnimation(TranslateTransform.YProperty, slide);
            items[i].e.BeginAnimation(OpacityProperty, fade);
        }
        // Sleeve "drops in": small scale bounce + lift.
        var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        SleeveScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0.9, 1, 900, back));
        SleeveScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(0.9, 1, 900, back));
        SleeveMove.BeginAnimation(TranslateTransform.YProperty, Anim(40, 0, 900, back));
    }

    void SetSpin(bool on)
    {
        if (on == _spinning) return;
        _spinning = on;
        double a = DiscRot.Angle % 360;
        DiscRot.BeginAnimation(RotateTransform.AngleProperty, null);
        DiscRot.Angle = a;
        if (on)
        {
            var start = Anim(a, a + 100, 1000, new QuadraticEase { EasingMode = EasingMode.EaseIn });
            start.Completed += (_, _) =>
            {
                if (!_spinning) return;
                double b = DiscRot.Angle % 360;
                DiscRot.BeginAnimation(RotateTransform.AngleProperty, null);
                DiscRot.Angle = b;
                var loop = new DoubleAnimation(b, b + 360, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever };
                DiscRot.BeginAnimation(RotateTransform.AngleProperty, loop);
            };
            DiscRot.BeginAnimation(RotateTransform.AngleProperty, start);
        }
        else
        {
            DiscRot.BeginAnimation(RotateTransform.AngleProperty, Anim(a, a + 50, 1500, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        }
    }

    void Render()
    {
        var st = _p.State;
        var t = st?.Track;
        bool on = _p.Connected && st != null;
        Offline.Text = Loc.T("state.offline", App.DeviceName);
        Offline.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        Main.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) { SetSpin(false); return; }

        bool playing = _p.IsPlaying;
        Device.Text = $"{App.DeviceName}   ·   {st!.Output}   ·   {(playing ? Loc.T("state.playing") : Loc.T("state.paused"))}"
                      + (App.Local.Enabled ? $"   ·   {App.Local.Status}" : "");
        if ((t?.Id ?? -1) != _shownTrackId)
        {
            _shownTrackId = t?.Id ?? -1;
            TitleT.Text = t?.Title is { Length: > 0 } ? t.Title : Loc.T("track.none");
            ArtistT.Text = t?.Artist ?? "";
            AlbumT.Text = t?.Album ?? "";
            FmtT.Text = t == null ? "" : Fmt.Format(t);
            AnimateText();
        }
        PlayBtn.Content = playing ? "" : "";
        if (!ReferenceEquals(_shownCover, _p.Cover))
        {
            _shownCover = _p.Cover;
            ShowCover(_p.Cover);
        }
        SetSpin(playing);

        var q = _p.Queue;
        int i = t == null ? -1 : q.ToList().FindIndex(x => x.Id == t.Id);
        string Line(int k) => i >= 0 && i + k < q.Count ? $"{i + k + 1}  ·  {q[i + k].Title}  —  {q[i + k].Artist}" : "";
        var qt = Line(1) + "\n" + Line(2) + "\n" + Line(3);
        if (qt != _queueText)
        {
            _queueText = qt;
            Q1.Text = Line(1);
            Q2.Text = Line(2);
            Q3.Text = Line(3);
            QueuePanel.BeginAnimation(OpacityProperty, Anim(0.15, 1, 700));
        }
        RenderProgress();
    }

    void RenderProgress()
    {
        var pos = _p.PositionNow;
        var dur = _p.DurationNow;
        double r = dur > 0 ? Math.Clamp((double)pos / dur, 0, 1) : 0;
        var w = ProgTrack.ActualWidth * r;
        if (Math.Abs(w - ProgFill.Width) > 40) { ProgFill.BeginAnimation(WidthProperty, null); ProgFill.Width = w; }
        else ProgFill.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(120)));
        PosT.Text = Fmt.Time(pos);
        DurT.Text = Fmt.Time(dur);
    }

    void RenderSpectrum()
    {
        var local = App.Local;
        bool active = App.S.ShowSpectrum && local.Enabled && local.PositionMs != null;
        if (active != _spectrumVisible)
        {
            _spectrumVisible = active;
            SpectrumCanvas.BeginAnimation(OpacityProperty, Anim(null, active ? 1 : 0, 800));
        }
        if (!active && SpectrumCanvas.Opacity < 0.01) return;
        var sp = local.Spectrum;
        sp.Update();
        double h = SpectrumCanvas.Height;
        for (int i = 0; i < _bars.Count && i < sp.Levels.Length; i++)
        {
            var bh = Math.Max(2, sp.Levels[i] * h);
            _bars[i].Height = bh;
            Canvas.SetTop(_bars[i], h - bh);
        }
    }

    // ---------- search ----------

    bool SearchOpen => SearchPanel.Visibility == Visibility.Visible;

    void OpenSearch()
    {
        SearchPanel.Visibility = Visibility.Visible;
        UpdateSearchStatus();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    void CloseSearch()
    {
        SearchPanel.Visibility = Visibility.Collapsed;
        _openAlbum = null;
        Focus();
    }

    string LibStatus()
    {
        if (_lib.Loading) return Loc.T("lib.loading", $"{_lib.Count:N0}");
        if (_lib.Count == 0) return Loc.T("lib.empty");
        if (_lib.TracksComplete) return Loc.T("lib.done", $"{_lib.Count:N0}", $"{_lib.TrackCount:N0}");
        var pct = _lib.Count == 0 ? 0 : 100.0 * _lib.CrawledAlbums / _lib.Count;
        var what = _lib.Upgrading ? Loc.T("lib.upgrading") : Loc.T("lib.building", $"{_lib.TrackCount:N0}");
        return Loc.T("lib.line", $"{_lib.Count:N0}", what, $"{pct:0}", _lib.Crawling ? "" : Loc.T("lib.stopped"));
    }

    void UpdateSearchStatus()
    {
        if (!SearchOpen || _openAlbum != null) return;
        if (SearchBox.Text.Trim().Length == 0) SearchStatus.Text = LibStatus();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (SearchOpen)
        {
            if (e.Key == Key.Escape) { CloseSearch(); e.Handled = true; }
            return;
        }
        switch (e.Key)
        {
            case Key.Escape: Hide(); e.Handled = true; break;
            case Key.Space: _ = _p.PlayOrPause(); e.Handled = true; break;
            case Key.Right: _ = _p.Next(); e.Handled = true; break;
            case Key.Left: _ = _p.Prev(); e.Handled = true; break;
            case Key.F5: _ = _lib.RefreshAsync(App.Client); e.Handled = true; break;
            case Key.L: App.Local.Enabled = !App.Local.Enabled; e.Handled = true; break;
            case Key.F3:
            case Key.OemQuestion:
            case Key.Divide:
                OpenSearch(); e.Handled = true; break;
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                OpenSearch(); e.Handled = true; break;
        }
    }

    void OnOpenSearch(object sender, RoutedEventArgs e) => OpenSearch();

    void OnSearchText(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _openAlbum = null;
        _ = RunSearch(SearchBox.Text);
    }

    async Task RunSearch(string q)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try { await Task.Delay(250, cts.Token); } catch { return; }
        q = q.Trim();
        if (q.Length == 0) { Results.ItemsSource = null; UpdateSearchStatus(); return; }

        var albums = _lib.Search(q, 20)
            .Select(a => new Hit(Loc.T("kind.album"), a.Name, a.Artist + (a.Year != "" ? "  ·  " + a.Year : ""), a, null, meta: Fmt.AlbumMeta(_lib.TracksOf(a.Id))))
            .ToList();
        var local = await Task.Run(() => _lib.SearchTracks(q, 60), cts.Token);
        if (cts.IsCancellationRequested) return;
        Hit ToHit(Track t) => new(Loc.T("kind.track"), t.Title, $"{t.Artist}  —  {t.Album}", null, t, meta: Fmt.Meta(t));
        Results.ItemsSource = albums.Concat(local.Select(ToHit)).ToList();
        _ = LoadThumbs(cts);
        var tail = _lib.TracksComplete ? "" : Loc.T("search.indexpct", $"{(_lib.Count == 0 ? 0 : 100.0 * _lib.CrawledAlbums / _lib.Count):0}");
        SearchStatus.Text = Loc.T("search.count", albums.Count, local.Count, tail);
        _ = MarkMissing(cts);

        if (_lib.TracksComplete || _lib.Upgrading) return; // upgrading = every track is already indexed, no need to ask the device
        var remote = await App.Client.SearchMusicAsync(q, 40);
        if (cts.IsCancellationRequested) return;
        var seen = local.Select(t => t.Id).ToHashSet();
        var extra = remote.Where(t => seen.Add(t.Id)).ToList();
        if (extra.Count == 0) return;
        Results.ItemsSource = albums.Concat(local.Select(ToHit)).Concat(extra.Select(ToHit)).ToList();
        _ = LoadThumbs(cts);
        _ = MarkMissing(cts);
        SearchStatus.Text = Loc.T("search.count", albums.Count, local.Count + extra.Count, tail);
    }

    /// <summary>
    /// The device's database still lists files that were deleted or moved on the NAS; playing one makes the device
    /// show its "kind reminder" dialog and skip. Check the listed tracks on the share and flag the missing ones.
    /// </summary>
    async Task MarkMissing(CancellationTokenSource cts)
    {
        if (Results.ItemsSource is not List<Hit> hits) return;
        var root = App.S.NasRoot;
        var flags = await Task.Run(() => hits.Select(h => h.Track is { Uri.Length: > 0 } t && NasPaths.ResolveFile(root, t.Uri) == null).ToArray(), cts.Token);
        if (cts.IsCancellationRequested || !ReferenceEquals(Results.ItemsSource, hits)) return;
        for (int i = 0; i < hits.Count; i++)
            if (flags[i])
            {
                hits[i].Missing = true;
                hits[i].Kind = Loc.T("kind.missing");
                hits[i].Meta = Loc.T("missing.meta") + hits[i].Meta;
            }
    }

    /// <summary>Fills in album thumbnails for the rows on screen, a few at a time, newest search wins.</summary>
    async Task LoadThumbs(CancellationTokenSource cts)
    {
        if (Results.ItemsSource is not List<Hit> hits) return;
        var root = App.S.NasRoot;
        foreach (var h in hits)
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(Results.ItemsSource, hits)) return;
            if (h.AlbumId == 0 || h.Thumb != null) continue;
            var first = h.Track ?? _lib.TracksOf(h.AlbumId).FirstOrDefault();
            try
            {
                var img = await CoverCache.GetAsync(h.AlbumId, first, root, App.Client, cts.Token);
                if (img != null) h.Thumb = img;
            }
            catch (OperationCanceledException) { return; }
            catch { }
        }
    }

    async Task OpenAlbum(Album a)
    {
        _openAlbum = a;
        SearchStatus.Text = Loc.T("album.loading", a.Name);
        var tracks = await App.Client.GetAlbumMusicsAsync(a.Id);
        if (_openAlbum != a) return;
        if (tracks == null) { SearchStatus.Text = Loc.T("device.noanswer"); return; }
        var list = new List<Hit> { new("←", Loc.T("back.title"), Loc.T("back.sub"), null, null, isBack: true) };
        list.AddRange(tracks.Select((t, i) => new Hit($"{i + 1}", t.Title, t.Artist, null, t, meta: Fmt.Meta(t))));
        Results.ItemsSource = list;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        _ = LoadThumbs(cts);
        _ = MarkMissing(cts);
        SearchStatus.Text = Loc.T("album.line", a.Name, a.Artist, tracks.Count);
        Results.SelectedIndex = tracks.Count > 0 ? 1 : 0;
        Results.Focus();
    }

    async Task Activate(Hit? h)
    {
        if (h == null) return;
        if (h.IsBack) { _openAlbum = null; await RunSearch(SearchBox.Text); Results.Focus(); return; }
        if (h.Album != null) { await OpenAlbum(h.Album); return; }
        if (h.Track != null)
        {
            if (h.Missing) { SearchStatus.Text = Loc.T("missing.select"); return; }
            var ok = await _p.Play(h.Track);
            SearchStatus.Text = ok ? Loc.T("play.now", h.Track.Title) : Loc.T("play.refused");
            if (ok) CloseSearch();
        }
    }

    void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && Results.Items.Count > 0)
        {
            Results.SelectedIndex = 0;
            Results.Focus();
            ((ListBoxItem?)Results.ItemContainerGenerator.ContainerFromIndex(0))?.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Results.Items.Count > 0)
        {
            _ = Activate((Hit?)(Results.SelectedItem ?? Results.Items[0]));
            e.Handled = true;
        }
    }

    void OnResultsKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _ = Activate(Results.SelectedItem as Hit); e.Handled = true; }
        else if (e.Key == Key.Up && Results.SelectedIndex <= 0) { SearchBox.Focus(); e.Handled = true; }
        else if (e.Key == Key.Back) { SearchBox.Focus(); }
    }

    void OnResultActivate(object sender, MouseButtonEventArgs e) => _ = Activate(Results.SelectedItem as Hit);

    void OnPlayPause(object sender, RoutedEventArgs e) => _ = _p.PlayOrPause();
    void OnNext(object sender, RoutedEventArgs e) => _ = _p.Next();
    void OnPrev(object sender, RoutedEventArgs e) => _ = _p.Prev();
}
