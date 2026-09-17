using System.IO;
using System.Windows.Media.Imaging;

namespace Eversolo;

/// <summary>
/// Small album thumbnails for search results: NAS folder art first, else the device's image for the first track.
/// Kept in memory and as 160 px JPEGs under %AppData%\Eversolo\covers so a library of 14k albums stays cheap.
/// </summary>
public static class CoverCache
{
    const int Px = 160;
    static readonly Dictionary<long, BitmapImage?> Mem = new();
    static readonly SemaphoreSlim Gate = new(2); // at most two fetches at a time (NAS + device)
    static string Dir => Path.Combine(Settings.Dir, "covers");

    public static async Task<BitmapImage?> GetAsync(long albumId, Track? first, string nasRoot, EversoloClient client, CancellationToken ct)
    {
        lock (Mem) if (Mem.TryGetValue(albumId, out var cached)) return cached;
        var file = Path.Combine(Dir, albumId + ".jpg");
        BitmapImage? img = null;
        try
        {
            if (File.Exists(file)) img = Load(await File.ReadAllBytesAsync(file, ct));
            else if (first != null)
            {
                await Gate.WaitAsync(ct);
                try
                {
                    byte[]? bytes = null;
                    if (first.Uri != "") bytes = await Task.Run(() => { try { return NasPaths.FolderArt(nasRoot, first.Uri); } catch { return null; } }, ct);
                    bytes ??= await client.GetImageAsync(first.Id, first.Type);
                    if (bytes != null)
                    {
                        var thumb = Shrink(bytes);
                        if (thumb != null)
                        {
                            Directory.CreateDirectory(Dir);
                            await File.WriteAllBytesAsync(file, thumb, ct);
                            img = Load(thumb);
                        }
                    }
                }
                finally { Gate.Release(); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { img = null; }
        lock (Mem) Mem[albumId] = img;
        return img;
    }

    static BitmapImage? Load(byte[] bytes)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = Px;
            bi.StreamSource = new MemoryStream(bytes);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    static byte[]? Shrink(byte[] src)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = Px;
            bi.StreamSource = new MemoryStream(src);
            bi.EndInit();
            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(bi));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
