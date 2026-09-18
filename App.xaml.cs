using System.Windows;
using Forms = System.Windows.Forms;

namespace Eversolo;

public partial class App : System.Windows.Application
{
    public static Settings S { get; private set; } = null!;
    public static EversoloClient Client { get; private set; } = null!;
    public static PlayerService Player { get; private set; } = null!;
    public static LibraryCache Library { get; } = new();
    public static LocalPlayer Local { get; private set; } = null!;
    /// <summary>Model name reported by the streamer (e.g. "DMP-A8"); "Eversolo" until known.</summary>
    public static string DeviceName { get; private set; } = "Eversolo";

    /// <summary>"Listen on this PC" only makes sense for files on the NAS; streams (Qobuz, Tidal, radio) have none.</summary>
    public static bool LocalAvailable => Player?.State?.Track is { } t && !t.IsStream;
    public static new App Current => (App)System.Windows.Application.Current;

    WidgetWindow? _widget;
    BigWindow? _big;
    Forms.NotifyIcon? _tray;

    // Single instance + command line: "EversoloForPC.exe" (or --big) shows the big screen on the running instance,
    // --playpause / --next / --prev / --local / --settings control it. Handy for hotkeys and Stream Deck buttons.
    static readonly string[] Commands = { "big", "playpause", "next", "prev", "local", "settings" };
    readonly List<EventWaitHandle> _signals = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var cmd = e.Args.Select(a => a.TrimStart('-', '/').ToLowerInvariant()).FirstOrDefault(Commands.Contains) ?? "big";
        bool first = true;
        foreach (var c in Commands)
        {
            var h = new EventWaitHandle(false, EventResetMode.AutoReset, $"Local\\{Settings.AppName}.{c}", out var createdNew);
            _signals.Add(h);
            if (!createdNew) first = false;
        }
        if (!first)
        {
            _signals[Array.IndexOf(Commands, cmd)].Set();
            Shutdown();
            return;
        }
        var handles = _signals.ToArray();
        new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var i = WaitHandle.WaitAny(handles);
                    Dispatcher.BeginInvoke(() => RunCommand(Commands[i]));
                }
            }
            catch { } // handles are closed on restart/exit
        }) { IsBackground = true }.Start();

        S = Settings.Load();
        Loc.Lang = S.Language == "tr" ? "tr" : "en";
        if (string.IsNullOrWhiteSpace(S.DeviceIp))
        {
            var ip = await EversoloClient.DiscoverAsync();
            if (ip != null) { S.DeviceIp = ip; S.Save(); }
        }
        Client = new EversoloClient(S.DeviceIp);
        if (!string.IsNullOrWhiteSpace(S.DeviceIp))
        {
            _ = Task.Run(async () =>
            {
                var model = await EversoloClient.GetModelAsync(S.DeviceIp);
                if (!string.IsNullOrWhiteSpace(model)) DeviceName = model!;
            });
            if (string.IsNullOrWhiteSpace(S.NasRoot))
            {
                var share = await Client.GetShareRootAsync();
                if (share != null) { S.NasRoot = share; S.Save(); }
            }
        }
        Player = new PlayerService(Client, Dispatcher) { PollMs = S.PollMs };
        Player.LocalCoverProvider = FolderArt;
        Player.DurationProbe = async uri =>
        {
            var file = NasPaths.ResolveFile(S.NasRoot, uri);
            var ff = LocalPlayer.FindFfmpeg(S.FfmpegPath);
            return file == null || ff == null ? 0 : await PlayerService.ProbeDurationMs(ff, file);
        };
        Local = new LocalPlayer(Player, S);
        Player.ExternalPosition = () => Local.Enabled ? Local.PositionMs : null;

        SetupTray();
        _widget = new WidgetWindow();
        _widget.Show();
        if (S.BigScreenOpenAtStart && Forms.Screen.AllScreens.Length > 1) ShowBig();
        if (string.IsNullOrWhiteSpace(S.DeviceIp)) ShowSettings();
        _ = Library.EnsureAsync(Client);
    }

    static byte[]? FolderArt(string uri) => NasPaths.FolderArt(S.NasRoot, uri);

    void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("menu.widget"), null, (_, _) => ToggleWidget());
        menu.Items.Add(Loc.T("menu.big"), null, (_, _) => ToggleBig());
        var local = new Forms.ToolStripMenuItem(Loc.T("menu.local.long")) { Checked = S.LocalPlay, CheckOnClick = true };
        local.Click += (_, _) => Local.Enabled = local.Checked;
        Local.Changed += () => { if (local.Checked != Local.Enabled) local.Checked = Local.Enabled; };
        Player.Changed += () => local.Enabled = LocalAvailable; // greyed out while a stream is playing
        menu.Items.Add(local);
        menu.Items.Add(Loc.T("menu.refresh"), null, (_, _) => _ = Library.RefreshAsync(Client));
        var crawl = new Forms.ToolStripMenuItem(Loc.T("menu.crawl.pause"));
        crawl.Click += (_, _) =>
        {
            if (Library.Crawling) Library.StopCrawl(); else Library.StartCrawl(Client);
        };
        Library.Changed += () => Dispatcher.BeginInvoke(() =>
            crawl.Text = Library.Crawling ? Loc.T("menu.crawl.pause") : Loc.T(Library.TracksComplete ? "menu.crawl.done" : "menu.crawl.resume"));
        menu.Items.Add(crawl);
        menu.Items.Add(Loc.T("menu.settings"), null, (_, _) => ShowSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("menu.exit"), null, (_, _) => ExitApp());
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = Settings.AppName,
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => ToggleWidget();
    }

    void RunCommand(string cmd)
    {
        switch (cmd)
        {
            case "big": ShowBig(); break;
            case "playpause": _ = Player.PlayOrPause(); break;
            case "next": _ = Player.Next(); break;
            case "prev": _ = Player.Prev(); break;
            case "local": if (LocalAvailable) Local.Enabled = !Local.Enabled; break;
            case "settings": ShowSettings(); break;
        }
    }

    public void ToggleWidget()
    {
        if (_widget == null) return;
        if (_widget.IsVisible) _widget.Hide(); else { _widget.Show(); _widget.Activate(); }
    }

    public void ShowBig()
    {
        if (_big == null)
        {
            _big = new BigWindow();
            _big.Closed += (_, _) => _big = null;
        }
        _big.ShowOnScreen(S.BigScreenIndex);
    }

    public void ToggleBig()
    {
        if (_big != null && _big.IsVisible) _big.Hide(); else ShowBig();
    }

    bool _settingsOpen;

    public void ShowSettings()
    {
        if (_settingsOpen) return;
        _settingsOpen = true;
        var langBefore = S.Language;
        bool saved;
        try { saved = new SettingsWindow().ShowDialog() == true; }
        finally { _settingsOpen = false; }
        if (!saved) return;
        if (S.Language != langBefore) { Restart(); return; } // static XAML strings are resolved at load: start fresh

        Client.Ip = S.DeviceIp;
        Player.PollMs = S.PollMs;
        Autostart.Apply(S.Autostart);
        if (_big != null && _big.IsVisible) _big.ShowOnScreen(S.BigScreenIndex);
        _big?.ApplyVisualSettings();
        Local.Restart(); // audio device / mode / DSD chain may have changed
        if (Library.Count == 0) _ = Library.EnsureAsync(Client, force: true);
    }

    /// <summary>Starts a fresh instance (after a language change) and exits this one.</summary>
    void Restart()
    {
        Autostart.Apply(S.Autostart);
        foreach (var h in _signals) { try { h.Dispose(); } catch { } } // free the single-instance names first
        _signals.Clear();
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false }); } catch { }
        ExitApp();
    }

    public void ExitApp()
    {
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Local.Dispose();
        Player.Dispose();
        Shutdown();
    }
}
