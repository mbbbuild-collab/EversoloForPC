using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using NAudio.CoreAudioApi;
using Forms = System.Windows.Forms;

namespace Eversolo;

public partial class SettingsWindow : Window
{
    readonly Settings _s = App.S;
    static readonly int[] Rates = { 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000 };
    readonly List<string> _devices = new();

    public SettingsWindow()
    {
        InitializeComponent();
        LangBox.SelectedIndex = _s.Language == "tr" ? 1 : 0;
        IpBox.Text = _s.DeviceIp;
        PollBox.Text = _s.PollMs.ToString();
        var screens = Forms.Screen.AllScreens;
        ScreenBox.Items.Add(Loc.T("monitor.auto"));
        for (int i = 0; i < screens.Length; i++)
        {
            var sc = screens[i];
            ScreenBox.Items.Add($"{i + 1}: {sc.Bounds.Width}×{sc.Bounds.Height}{(sc.Primary ? Loc.T("monitor.primary") : "")}");
        }
        ScreenBox.SelectedIndex = _s.BigScreenIndex >= 0 && _s.BigScreenIndex < screens.Length ? _s.BigScreenIndex + 1 : 0;
        BigAtStart.IsChecked = _s.BigScreenOpenAtStart;
        AutoStart.IsChecked = _s.Autostart;
        VinylBox.IsChecked = _s.Vinyl;
        KenBurnsBox.IsChecked = _s.KenBurns;
        SpectrumBox.IsChecked = _s.ShowSpectrum;

        DeviceBox.Items.Add(Loc.T("dev.default"));
        _devices.Add("");
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                DeviceBox.Items.Add(d.FriendlyName);
                _devices.Add(d.FriendlyName);
            }
        }
        catch { }
        var want = _s.AudioDevice?.Trim() ?? "";
        int sel = want.Length == 0 ? 0 : _devices.FindIndex(n => n.Length > 0 && n.Contains(want, StringComparison.OrdinalIgnoreCase));
        DeviceBox.SelectedIndex = sel < 0 ? 0 : sel;
        ExclusiveBox.IsChecked = _s.Exclusive;
        foreach (var r in Rates) RateBox.Items.Add($"{r / 1000.0:0.#} kHz");
        RateBox.SelectedIndex = Math.Max(0, Array.IndexOf(Rates, _s.MaxSampleRate));
        DsdGainBox.Text = _s.DsdGainDb.ToString("0.#", CultureInfo.InvariantCulture);
        DsdLowpassBox.Text = _s.DsdLowpassHz.ToString();
        LimiterBox.IsChecked = _s.DsdLimiter;
        NasBox.Text = _s.NasRoot;
        FfmpegBox.Text = _s.FfmpegPath;
        var ff = LocalPlayer.FindFfmpeg(_s.FfmpegPath);
        FfmpegStatus.Text = ff == null ? Loc.T("ff.notfound") : Loc.T("ff.found", ff);
        Status.Text = _s.DeviceIp == "" ? Loc.T("st.nodevice") : "";
    }

    async void OnDiscover(object sender, RoutedEventArgs e)
    {
        DiscoverBtn.IsEnabled = false;
        Status.Text = Loc.T("st.scanning");
        var ip = await EversoloClient.DiscoverAsync();
        if (ip != null)
        {
            IpBox.Text = ip;
            Status.Text = Loc.T("st.found", ip, await EversoloClient.GetModelAsync(ip));
        }
        else Status.Text = Loc.T("st.notfound");
        DiscoverBtn.IsEnabled = true;
    }

    async void OnSave(object sender, RoutedEventArgs e)
    {
        var ip = IpBox.Text.Trim();
        if (ip == "") { Status.Text = Loc.T("st.ipempty"); return; }
        if (!double.TryParse(DsdGainBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var gain) || gain > 0 || gain < -24)
        { Status.Text = Loc.T("st.gain"); return; }
        if (!int.TryParse(DsdLowpassBox.Text, out var lp) || lp < 0 || lp > 100000)
        { Status.Text = Loc.T("st.lp"); return; }
        Status.Text = Loc.T("st.verifying");
        var model = await EversoloClient.GetModelAsync(ip);
        if (model == null) Status.Text = Loc.T("st.noapi", ip);

        _s.Language = (LangBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "en";
        _s.DeviceIp = ip;
        _s.PollMs = int.TryParse(PollBox.Text, out var p) ? Math.Clamp(p, 250, 10000) : 1000;
        _s.BigScreenIndex = ScreenBox.SelectedIndex - 1;
        _s.BigScreenOpenAtStart = BigAtStart.IsChecked == true;
        _s.Autostart = AutoStart.IsChecked == true;
        _s.Vinyl = VinylBox.IsChecked == true;
        _s.KenBurns = KenBurnsBox.IsChecked == true;
        _s.ShowSpectrum = SpectrumBox.IsChecked == true;
        _s.AudioDevice = DeviceBox.SelectedIndex > 0 ? _devices[DeviceBox.SelectedIndex] : "";
        _s.Exclusive = ExclusiveBox.IsChecked == true;
        _s.MaxSampleRate = Rates[Math.Max(0, RateBox.SelectedIndex)];
        _s.DsdGainDb = gain;
        _s.DsdLowpassHz = lp;
        _s.DsdLimiter = LimiterBox.IsChecked == true;
        _s.NasRoot = NasBox.Text.Trim();
        _s.FfmpegPath = FfmpegBox.Text.Trim();
        _s.Save();
        DialogResult = true;
    }
}
