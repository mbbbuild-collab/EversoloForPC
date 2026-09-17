using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Eversolo;

/// <summary>
/// Album list + full track index cached on disk; searched locally with Turkish-insensitive matching.
/// Tracks are crawled album by album in the background (the device has no bulk track endpoint) and
/// appended to tracks.jsonl one line per album, so the crawl is resumable and cheap to persist.
/// </summary>
public sealed class LibraryCache
{
    sealed record AlbumEntry(Album Album, string Key);
    sealed record TrackEntry(Track Track, string Key);
    sealed record AlbumsDisk(DateTime At, List<Album> Albums);
    sealed record TracksLine(long A, List<Track> T);

    // One worker with a pause between albums: the crawl is background work and the device's music service also
    // feeds its own screen, the phone app's screen mirror, and playback; two hungry workers made those stutter.
    const int Workers = 1;
    const int GapMs = 250;
    static string AlbumsPath => Path.Combine(Settings.Dir, "albums.json");
    static string TracksPath => Path.Combine(Settings.Dir, "tracks.jsonl");

    readonly object _lock = new();
    List<AlbumEntry> _albums = new();
    List<TrackEntry> _tracks = new();
    Dictionary<long, List<Track>> _byAlbum = new();
    HashSet<long> _doneAlbums = new();

    /// <summary>Indexed tracks of an album (copy), for search-result details and thumbnails.</summary>
    public List<Track> TracksOf(long albumId)
    {
        lock (_lock) return _byAlbum.TryGetValue(albumId, out var l) ? new List<Track>(l) : new List<Track>();
    }
    CancellationTokenSource? _crawl;

    public int Count => _albums.Count;
    public int TrackCount { get { lock (_lock) return _tracks.Count; } }
    public int CrawledAlbums { get { lock (_lock) return _doneAlbums.Count; } }
    public bool Crawling => _crawl != null && !_crawl.IsCancellationRequested;
    public bool TracksComplete => _albums.Count > 0 && CrawledAlbums >= _albums.Count;
    public DateTime LoadedAt { get; private set; }
    public bool Loading { get; private set; }
    public event Action? Changed;

    public async Task EnsureAsync(EversoloClient c, bool force = false)
    {
        if (_albums.Count == 0 && File.Exists(AlbumsPath))
        {
            try
            {
                var d = JsonSerializer.Deserialize<AlbumsDisk>(await File.ReadAllTextAsync(AlbumsPath));
                if (d != null) { SetAlbums(d.Albums); LoadedAt = d.At; }
            }
            catch { }
        }
        if (TrackCount == 0 && File.Exists(TracksPath))
        {
            await Task.Run(LoadTracks);
            Changed?.Invoke();
        }
        if (force || _albums.Count == 0 || DateTime.UtcNow - LoadedAt > TimeSpan.FromDays(1))
            await RefreshAsync(c);
        if (!TracksComplete) StartCrawl(c);
    }

    /// <summary>Albums whose stored tracks predate the duration/uri fields; they are re-crawled in the background.</summary>
    public int UpgradePending { get; private set; }
    public bool Upgrading => TrackCount > 0 && !TracksComplete;

    void LoadTracks()
    {
        var perAlbum = new Dictionary<long, List<Track>>();
        try
        {
            foreach (var line in File.ReadLines(TracksPath))
            {
                if (line.Length == 0) continue;
                TracksLine? l;
                try { l = JsonSerializer.Deserialize<TracksLine>(line); } catch { continue; }
                if (l != null) perAlbum[l.A] = l.T; // last line for an album wins (re-crawls append newer lines)
            }
        }
        catch { }
        var tracks = new List<TrackEntry>();
        var done = new HashSet<long>();
        int pending = 0;
        foreach (var (album, list) in perAlbum)
        {
            tracks.AddRange(list.Select(t => new TrackEntry(t, TrackKey(t))));
            // Older index lines lack duration/uri: keep them searchable but let the crawler refresh that album.
            if (list.Count == 0 || list.All(t => t.Duration > 0 && t.Uri != "")) done.Add(album); else pending++;
        }
        lock (_lock) { _tracks = tracks; _byAlbum = perAlbum; _doneAlbums = done; UpgradePending = pending; }
        Log($"loaded index: {tracks.Count} tracks from {perAlbum.Count} albums ({pending} to upgrade), managed heap {GC.GetTotalMemory(false) / 1048576} MB");
    }

    public async Task RefreshAsync(EversoloClient c)
    {
        if (Loading) return;
        Loading = true;
        Changed?.Invoke();
        try
        {
            var list = new List<Album>();
            int start = 0;
            while (true)
            {
                var (total, page) = await c.GetAlbumsAsync(start, 500);
                if (page.Count == 0) break;
                list.AddRange(page);
                start += page.Count;
                if (start >= total) break;
            }
            if (list.Count > 0)
            {
                SetAlbums(list);
                LoadedAt = DateTime.UtcNow;
                Directory.CreateDirectory(Settings.Dir);
                await File.WriteAllTextAsync(AlbumsPath, JsonSerializer.Serialize(new AlbumsDisk(LoadedAt, list)));
            }
        }
        catch { }
        Loading = false;
        Changed?.Invoke();
        if (!TracksComplete) StartCrawl(c);
    }

    /// <summary>Fetches tracks for every album not crawled yet with a few parallel workers; each album is appended to disk as it lands.</summary>
    public void StartCrawl(EversoloClient c)
    {
        if (Crawling) return;
        var cts = _crawl = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            List<Album> pending;
            lock (_lock) pending = _albums.Select(e => e.Album).Where(a => !_doneAlbums.Contains(a.Id)).ToList();
            var queue = new System.Collections.Concurrent.ConcurrentQueue<Album>(pending);
            int done = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Log($"crawl start: {CrawledAlbums}/{_albums.Count} albums done, {TrackCount} tracks, {pending.Count} pending, {Workers} workers");

            var perAlbumFailures = new System.Collections.Concurrent.ConcurrentDictionary<long, int>();
            async Task Worker(int id)
            {
                int failures = 0;
                while (!cts.IsCancellationRequested && queue.TryDequeue(out var a))
                {
                    var t0 = sw.ElapsedMilliseconds;
                    var tracks = await c.GetAlbumMusicsAsync(a.Id);
                    var ms = sw.ElapsedMilliseconds - t0;
                    if (tracks == null && ms < 4000 && perAlbumFailures.AddOrUpdate(a.Id, 1, (_, n) => n + 1) >= 3)
                    {
                        // Fast, repeated failure for this one album (device returns an error for it): record it as empty
                        // so it can never block completion, instead of treating it as a stalled device.
                        Log($"w{id}: album {a.Id} fails consistently, recording as empty");
                        tracks = new List<Track>();
                    }
                    if (tracks == null)
                    {
                        queue.Enqueue(a); // retry later
                        failures++;
                        // Timed-out requests keep running on the device, so after a few failures go fully quiet
                        // for 5 minutes to let its music database drain instead of piling more queries on it.
                        var wait = failures >= 5 ? 300_000 : Math.Min(60_000, 2_000 * failures);
                        Log($"w{id}: album {a.Id} no answer after {ms} ms (failure #{failures}), waiting {wait / 1000} s");
                        try { await Task.Delay(wait, cts.Token); } catch { return; }
                        continue;
                    }
                    failures = 0;
                    var line = JsonSerializer.Serialize(new TracksLine(a.Id, tracks));
                    lock (_lock)
                    {
                        _tracks.RemoveAll(e => e.Track.AlbumId == a.Id); // replace an older copy of this album, if any
                        _tracks.AddRange(tracks.Select(t => new TrackEntry(t, TrackKey(t))));
                        _byAlbum[a.Id] = tracks;
                        _doneAlbums.Add(a.Id);
                        try
                        {
                            Directory.CreateDirectory(Settings.Dir);
                            File.AppendAllText(TracksPath, line + "\n");
                        }
                        catch (Exception ex) { Log("write error: " + ex.Message); }
                    }
                    var n = Interlocked.Increment(ref done);
                    if (n % 100 == 0)
                    {
                        Log($"{CrawledAlbums}/{_albums.Count} albums, {TrackCount} tracks, {n / Math.Max(1, sw.Elapsed.TotalSeconds):0.00} albums/s, last call {ms} ms, heap {GC.GetTotalMemory(false) / 1048576} MB, ws {Environment.WorkingSet / 1048576} MB");
                        Changed?.Invoke();
                    }
                    try { await Task.Delay(GapMs, cts.Token); } catch { return; }
                }
            }

            try { await Task.WhenAll(Enumerable.Range(1, Workers).Select(Worker)); }
            catch (Exception ex) { Log("crawl error: " + ex.Message); }
            Log($"crawl stop: {CrawledAlbums}/{_albums.Count} albums, {TrackCount} tracks in {sw.Elapsed:hh\\:mm\\:ss}");
            _crawl = null;
            Changed?.Invoke();
        });
    }

    public void StopCrawl() => _crawl?.Cancel();

    static readonly object LogLock = new();
    static void Log(string msg)
    {
        try
        {
            lock (LogLock)
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(Path.Combine(Settings.Dir, "crawl.log"), $"{DateTime.Now:HH:mm:ss} {msg}\n");
            }
        }
        catch { }
    }

    void SetAlbums(List<Album> albums) =>
        _albums = albums.Select(a => new AlbumEntry(a, Norm(a.Name) + "\n" + Norm(a.Artist))).ToList();

    static string TrackKey(Track t) => Norm(t.Title) + "\n" + Norm(t.Artist) + "\n" + Norm(t.Album);

    public static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Replace('I', 'i').Replace('İ', 'i').ToLowerInvariant())
        {
            var mapped = ch switch
            {
                'ı' => 'i', // dotless i
                'ş' => 's', // ş
                'ğ' => 'g', // ğ
                'ç' => 'c', // ç
                'ö' => 'o', // ö
                'ü' => 'u', // ü
                _ => ch
            };
            foreach (var d in mapped.ToString().Normalize(NormalizationForm.FormD))
                if (CharUnicodeInfo.GetUnicodeCategory(d) != UnicodeCategory.NonSpacingMark) sb.Append(d);
        }
        return sb.ToString();
    }

    static string[] Words(string q) => Norm(q).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public List<Album> Search(string q, int max = 30)
    {
        var words = Words(q);
        if (words.Length == 0) return new List<Album>();
        var n = string.Join(' ', words);
        return _albums
            .Where(e => words.All(w => e.Key.Contains(w)))
            .OrderBy(e => e.Key.StartsWith(n) || e.Key.Contains("\n" + n) ? 0 : 1)
            .ThenBy(e => e.Album.Name)
            .Take(max)
            .Select(e => e.Album)
            .ToList();
    }

    public List<Track> SearchTracks(string q, int max = 60)
    {
        var words = Words(q);
        if (words.Length == 0) return new List<Track>();
        var n = string.Join(' ', words);
        TrackEntry[] snap;
        lock (_lock) snap = _tracks.ToArray();
        return snap
            .Where(e => words.All(w => e.Key.Contains(w)))
            .OrderBy(e => e.Key.StartsWith(n) ? 0 : e.Key.Contains("\n" + n) ? 1 : 2)
            .ThenBy(e => e.Track.Title)
            .Take(max)
            .Select(e => e.Track)
            .ToList();
    }
}
