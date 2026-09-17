using System.IO;

namespace Eversolo;

/// <summary>Maps the device's share-relative track uri to a UNC path on the NAS, tolerating renamed album folders.</summary>
public static class NasPaths
{
    public static string ToUnc(string nasRoot, string uri) =>
        Path.Combine(nasRoot.TrimEnd('\\'), uri.TrimStart('/').Replace('/', '\\'));

    /// <summary>
    /// Exact path if it exists; otherwise the same file name inside a sibling folder of the album folder
    /// (the device's database keeps the old folder name after a rename on the NAS). null = not found.
    /// </summary>
    public static string? ResolveFile(string nasRoot, string uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var full = ToUnc(nasRoot, uri);
        try
        {
            if (File.Exists(full)) return full;
            var name = Path.GetFileName(full);
            var key = NameKey(name);
            var dir0 = Path.GetDirectoryName(full);
            var parent = dir0 == null ? null : Path.GetDirectoryName(dir0);
            var dirs = new List<string>();
            if (dir0 != null && Directory.Exists(dir0)) dirs.Add(dir0);
            if (parent != null && Directory.Exists(parent))
                dirs.AddRange(Directory.EnumerateDirectories(parent).Where(d => !string.Equals(d, dir0, StringComparison.OrdinalIgnoreCase)));
            // 1) exact file name in the album folder or a renamed sibling folder
            foreach (var dir in dirs)
            {
                var cand = Path.Combine(dir, name);
                if (File.Exists(cand)) return cand;
            }
            // 2) same name up to punctuation differences (’ vs ', – vs -, case, diacritics)
            foreach (var dir in dirs)
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (NameKey(Path.GetFileName(f)) == key) return f;
        }
        catch { }
        return null;
    }

    /// <summary>Loose comparison key for file names: the device's database and the NAS may differ in apostrophes, dashes, case, accents.</summary>
    static string NameKey(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var raw in s.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(raw) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            var ch = raw switch
            {
                '‘' or '’' or '‚' or '′' or '`' or '´' => '\'',
                '“' or '”' or '„' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                ' ' => ' ',
                _ => raw
            };
            sb.Append(char.ToLowerInvariant(ch));
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>cover/folder/front/album art next to the file, else the largest image in that folder.</summary>
    public static byte[]? FolderArt(string nasRoot, string uri)
    {
        var file = ResolveFile(nasRoot, uri);
        var dir = file == null ? null : Path.GetDirectoryName(file);
        if (dir == null || !Directory.Exists(dir)) return null;
        string[] preferred = { "cover", "folder", "front", "album", "albumart", "artwork" };
        string[] exts = { ".jpg", ".jpeg", ".png" };
        foreach (var n in preferred)
            foreach (var e in exts)
            {
                var p = Path.Combine(dir, n + e);
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
        var any = Directory.EnumerateFiles(dir)
            .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
        return any == null ? null : File.ReadAllBytes(any);
    }
}
