namespace Eversolo;

public static class Fmt
{
    public static string Time(long ms)
    {
        var s = Math.Max(0, ms) / 1000;
        return $"{s / 60}:{s % 60:00}";
    }

    public static string Format(Track t)
    {
        var parts = new List<string>();
        if (t.Extension != "") parts.Add(t.Extension.ToUpperInvariant());
        var sr = Rate(t.SampleRate);
        if (sr != "") parts.Add(t.Bits > 0 ? $"{sr} / {t.Bits} bit" : sr);
        var br = Bitrate(t.Bitrate);
        if (br != "") parts.Add(br);
        return string.Join("   ·   ", parts);
    }

    /// <summary>Short "where is this copy" hint from the share-relative uri: first folder + album folder.</summary>
    public static string FolderHint(string uri)
    {
        if (string.IsNullOrEmpty(uri)) return "";
        var parts = uri.Trim('/').Split('/');
        if (parts.Length < 3) return string.Join('/', parts.Take(parts.Length - 1));
        return parts[0] + "/…/" + parts[^2];
    }

    /// <summary>Compact one-line description for search results: format · duration · folder.</summary>
    public static string Meta(Track t)
    {
        var parts = new List<string>();
        var f = Format(t);
        if (f != "") parts.Add(f);
        if (t.Duration > 0) parts.Add(Time(t.Duration));
        var h = FolderHint(t.Uri);
        if (h != "") parts.Add(h);
        return string.Join("   ·   ", parts);
    }

    /// <summary>Album line for search results, derived from its indexed tracks: count · format · total time · folder.</summary>
    public static string AlbumMeta(List<Track> tracks)
    {
        if (tracks.Count == 0) return "";
        var parts = new List<string> { Loc.T("fmt.tracks", tracks.Count) };
        var f = Format(tracks[0]);
        if (f != "") parts.Add(f);
        var total = tracks.Sum(t => t.Duration);
        if (total > 0) parts.Add(Time(total));
        var h = FolderHint(tracks[0].Uri);
        if (h != "") parts.Add(h);
        return string.Join("   ·   ", parts);
    }

    static string Rate(string s) => long.TryParse(s, out var n) ? (n > 0 ? $"{n / 1000.0:0.#} kHz" : "") : s;

    static string Bitrate(string s) => long.TryParse(s, out var n)
        ? (n <= 0 ? "" : n >= 1_000_000 ? $"{n / 1_000_000.0:0.00} Mbps" : $"{n / 1000} kbps")
        : s;
}
