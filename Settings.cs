using System.IO;
using System.Text.Json;

namespace Eversolo;

public sealed class Settings
{
    /// <summary>UI language: "en" (default) or "tr".</summary>
    public string Language { get; set; } = "en";
    public string DeviceIp { get; set; } = "";
    public int PollMs { get; set; } = 1000;
    public double WidgetLeft { get; set; } = double.NaN;
    public double WidgetTop { get; set; } = double.NaN;
    public bool WidgetMini { get; set; }
    /// <summary>-1 = otomatik: ana olmayan ilk monitör.</summary>
    public int BigScreenIndex { get; set; } = -1;

    public static int ResolveScreen(int idx)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (screens.Length == 0) return 0;
        if (idx >= 0 && idx < screens.Length) return idx;
        for (int i = 0; i < screens.Length; i++) if (!screens[i].Primary) return i;
        return 0;
    }
    public bool BigScreenOpenAtStart { get; set; } = true;
    public bool Autostart { get; set; }
    /// <summary>UNC root of the device's music share on the NAS; track "uri" values are appended to it.</summary>
    public string NasRoot { get; set; } = ""; // empty = detected from the device's music folder (nfs://host/share -> \\host\share)
    public bool LocalPlay { get; set; }
    public double LocalVolume { get; set; } = 0.8;
    /// <summary>Substring of the Windows render device name to use (e.g. "DragonFly"); empty = default device.</summary>
    public string AudioDevice { get; set; } = "";
    /// <summary>WASAPI exclusive mode: bypasses the Windows mixer/resampler (bit-perfect PCM).</summary>
    public bool Exclusive { get; set; } = true;
    /// <summary>Max PCM rate the DAC accepts; higher-rate files and DSD are resampled down to a multiple of this family.</summary>
    public int MaxSampleRate { get; set; } = 96000;
    public string FfmpegPath { get; set; } = "";
    /// <summary>Gain applied to DSD before integer conversion (SACD peaks can exceed 0 dBFS); a true-peak limiter at -1 dB catches the rest.</summary>
    public double DsdGainDb { get; set; } = -3;
    /// <summary>Low-pass for DSD's ultrasonic shaping noise. 30 kHz / 4 poles is inaudible in-band (-0.2 dB at 20 kHz); 0 = off.</summary>
    public int DsdLowpassHz { get; set; } = 30000;
    /// <summary>Transparent true-peak limiter at -1 dBFS on DSD (catches peaks the gain did not).</summary>
    public bool DsdLimiter { get; set; } = true;
    public bool ShowSpectrum { get; set; } = true;
    public bool Vinyl { get; set; } = true;
    public bool KenBurns { get; set; } = true;

    public const string AppName = "EversoloForPC";

    /// <summary>%AppData%\EversoloForPC. A data folder from before the rename (%AppData%\Eversolo) is moved over once.</summary>
    public static string Dir
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(root, AppName);
            if (!_migrated)
            {
                _migrated = true;
                try
                {
                    var old = Path.Combine(root, "Eversolo");
                    if (!Directory.Exists(dir) && Directory.Exists(old)) Directory.Move(old, dir);
                }
                catch { }
            }
            return dir;
        }
    }
    static bool _migrated;
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opt));
        }
        catch { }
    }
}

public static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key, true);
            if (k == null) return;
            k.DeleteValue("Eversolo", false); // entry from before the rename
            if (on) k.SetValue(Settings.AppName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(Settings.AppName, false);
        }
        catch { }
    }
}
