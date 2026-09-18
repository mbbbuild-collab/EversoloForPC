using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Eversolo;

public sealed record Track(long Id, int Type, string Title, string Artist, string Album, long AlbumId,
    string Bitrate, string SampleRate, int Bits, string Extension, int Channels, string Uri = "", long Duration = 0,
    string AlbumArt = "", string Stream = "")
{
    /// <summary>True for Qobuz / Tidal / radio etc.: no file on the NAS, cover comes from a URL.</summary>
    public bool IsStream => Stream != "" || Uri.StartsWith("http", StringComparison.OrdinalIgnoreCase);
}

public sealed record Album(long Id, string Name, string Artist, string Year);

public sealed record PlayerState(bool Playing, int RawState, long PositionMs, long DurationMs, Track? Track,
    string Output, string VolumeDisplay, int OutputRate, long QueueChangedAt);

/// <summary>Eversolo / Zidoo control API on port 9529. Read endpoints verified on DMP-A8 fw 1.5.75.</summary>
public sealed class EversoloClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    public string Ip { get; set; }
    string M => $"http://{Ip}:9529/ZidooMusicControl/v2/";
    public EversoloClient(string ip) { Ip = ip; }

    async Task<JsonDocument?> GetJson(string url)
    {
        try { return JsonDocument.Parse(await Http.GetStringAsync(url)); }
        catch { return null; }
    }

    static string S(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

    static long L(JsonElement e, string n)
    {
        if (!e.TryGetProperty(n, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var l) ? l : (long)v.GetDouble();
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var p) ? p : 0;
    }

    static Track ParseTrack(JsonElement e)
    {
        // getState uses "sampleRate": "44.1 kHz"; album/queue lists use "SampleRate": 44100 (and "sampleRateNumber").
        var rate = S(e, "sampleRate");
        if (rate == "") { var n = L(e, "SampleRate"); if (n == 0) n = L(e, "sampleRateNumber"); if (n > 0) rate = n.ToString(); }
        return new(L(e, "id"), (int)L(e, "type"), S(e, "title"), S(e, "artist"),
            S(e, "album"), L(e, "albumId"), S(e, "bitrate"), rate, (int)L(e, "bits"), S(e, "extension"),
            (int)L(e, "channels"), S(e, "uri"), L(e, "duration"), S(e, "albumArtBig") is { Length: > 0 } big ? big : S(e, "albumArt"), S(e, "streamId"));
    }

    /// <summary>Downloads bytes from an http(s) URL (streaming-service cover art); null on failure.</summary>
    public static async Task<byte[]?> GetUrlBytesAsync(string url)
    {
        try
        {
            var b = await Http.GetByteArrayAsync(url);
            return b.Length > 200 ? b : null;
        }
        catch { return null; }
    }

    public async Task<PlayerState?> GetStateAsync()
    {
        using var d = await GetJson(M + "getState");
        if (d == null) return null;
        var r = d.RootElement;
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("state", out _)) return null;
        Track? t = r.TryGetProperty("playingMusic", out var pm) && pm.ValueKind == JsonValueKind.Object ? ParseTrack(pm) : null;
        string output = "", vol = "";
        if (r.TryGetProperty("volumeData", out var vd) && vd.ValueKind == JsonValueKind.Object)
        {
            output = S(vd, "volumeTag");
            vol = S(vd, "display");
        }
        var st = (int)L(r, "state"); // verified on DMP-A8: 3 = playing (position advances), 4 = paused
        return new PlayerState(st == 3, st, L(r, "position"), L(r, "duration"), t, output, vol,
            (int)L(r, "currentOutput"), L(r, "playQueueChangedTime"));
    }

    /// <summary>null = request failed (device unreachable/stalled); empty list = valid but empty.</summary>
    static List<Track>? Tracks(JsonDocument? d)
    {
        if (d == null || d.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (!d.RootElement.TryGetProperty("array", out var a) || a.ValueKind != JsonValueKind.Array)
            return d.RootElement.TryGetProperty("status", out _) && !d.RootElement.TryGetProperty("total", out _) ? null : new List<Track>();
        return a.EnumerateArray().Select(ParseTrack).ToList();
    }

    public async Task<List<Track>> GetQueueAsync(int count = 300)
    {
        using var d = await GetJson(M + $"getPlayQueue?start=0&count={count}");
        return Tracks(d) ?? new List<Track>();
    }

    public async Task<byte[]?> GetImageAsync(long id, int musicType)
    {
        try
        {
            var b = await Http.GetByteArrayAsync(M + $"getImage?id={id}&music_type={musicType}&target=16");
            return b.Length > 200 && b[0] != 0x7B ? b : null; // 0x7B = '{' -> JSON error body
        }
        catch { return null; }
    }

    public async Task<(int total, List<Album> items)> GetAlbumsAsync(int start, int count)
    {
        using var d = await GetJson(M + $"getAlbums?start={start}&count={count}");
        if (d == null || !d.RootElement.TryGetProperty("array", out var a) || a.ValueKind != JsonValueKind.Array)
            return (0, new List<Album>());
        var list = a.EnumerateArray().Select(e => new Album(L(e, "id"), S(e, "name"), S(e, "artist"), S(e, "pubDate"))).ToList();
        return ((int)L(d.RootElement, "total"), list);
    }

    /// <summary>null when the device did not answer (caller should back off).</summary>
    public async Task<List<Track>?> GetAlbumMusicsAsync(long albumId)
    {
        using var d = await GetJson(M + $"getAlbumMusics?id={albumId}&start=0&count=500");
        return Tracks(d);
    }

    public async Task<List<Track>> SearchMusicAsync(string key, int count = 50)
    {
        using var d = await GetJson(M + $"searchMusic?key={WebUtility.UrlEncode(key)}&start=0&count={count}");
        return Tracks(d) ?? new List<Track>();
    }

    async Task<bool> Cmd(string u)
    {
        using var d = await GetJson(M + u);
        return d != null && L(d.RootElement, "status") == 200;
    }

    public Task<bool> PlayOrPauseAsync() => Cmd("playOrPause");

    /// <summary>Zidoo remote-control key (Key.MediaPlay, Key.MediaPause, Key.MediaNext, ...); works even when the music API's state is stale.</summary>
    public async Task<bool> SendKeyAsync(string key)
    {
        using var d = await GetJson($"http://{Ip}:9529/ZidooControlCenter/RemoteControl/sendkey?key={key}");
        return d != null && L(d.RootElement, "status") == 200;
    }
    public Task<bool> NextAsync() => Cmd("playNext");
    public Task<bool> PrevAsync() => Cmd("playLast");

    /// <summary>
    /// Plays a track. With a known album: loads the whole album into the queue and starts at that track
    /// (type 4 = album, as in wizmo2/zidoo-player). Otherwise queues just that track via playMusics.
    /// </summary>
    public Task<bool> PlayTrackAsync(Track t) => t.AlbumId > 0
        ? Cmd($"playMusic?type=4&id={t.AlbumId}&musicId={t.Id}&music_type=0&trackIndex=0&sort=0")
        : Cmd($"playMusics?ids={t.Id}&musicId={t.Id}&trackIndex=-1");

    /// <summary>
    /// UNC root of the device's first network music folder, e.g. "nfs://10.0.0.5/Music?..." or "smb://10.0.0.5/Music"
    /// becomes "\\10.0.0.5\Music" (NAS shares are normally reachable over SMB under the same name). null if none.
    /// </summary>
    public async Task<string?> GetShareRootAsync()
    {
        using var d = await GetJson(M + "getFolders?start=0&count=20");
        if (d == null || d.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var f in d.RootElement.EnumerateArray())
        {
            var url = S(f, "url");
            var i = url.IndexOf("://", StringComparison.Ordinal);
            if (i < 0) continue;
            var rest = url[(i + 3)..];
            var q = rest.IndexOf('?');
            if (q >= 0) rest = rest[..q];
            var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return $@"\\{parts[0]}\{string.Join('\\', parts.Skip(1))}";
        }
        return null;
    }

    public static async Task<string?> GetModelAsync(string ip)
    {
        try
        {
            using var cts = new CancellationTokenSource(2500);
            var s = await Http.GetStringAsync($"http://{ip}:9529/ZidooControlCenter/getModel", cts.Token);
            using var d = JsonDocument.Parse(s);
            var m = S(d.RootElement, "model");
            return m == "" ? null : m;
        }
        catch { return null; }
    }

    /// <summary>Scans every local /24 for port 9529 and returns the first host that answers getModel.</summary>
    public static async Task<string?> DiscoverAsync()
    {
        var bases = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.PrefixLength >= 16)
            .Select(u => string.Join('.', u.Address.ToString().Split('.').Take(3)))
            .Distinct().ToList();
        foreach (var b in bases)
        {
            var tasks = Enumerable.Range(1, 254).Select(async i =>
            {
                var ip = $"{b}.{i}";
                try
                {
                    using var c = new TcpClient();
                    var t = c.ConnectAsync(ip, 9529);
                    if (await Task.WhenAny(t, Task.Delay(700)) != t || !c.Connected) return null;
                }
                catch { return null; }
                return await GetModelAsync(ip) != null ? ip : null;
            }).ToList();
            var hits = await Task.WhenAll(tasks);
            var hit = hits.FirstOrDefault(h => h != null);
            if (hit != null) return hit;
        }
        return null;
    }
}
