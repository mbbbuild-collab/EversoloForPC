using System.Windows.Markup;

namespace Eversolo;

/// <summary>UI strings. English is the default; Turkish is selectable in Settings ("Language").</summary>
public static class Loc
{
    public static string Lang { get; set; } = "en";

    public static string T(string key, params object?[] args)
    {
        var s = Map.TryGetValue(key, out var v) ? (Lang == "tr" ? v.tr : v.en) : key;
        return args.Length == 0 ? s : string.Format(s, args);
    }

    static readonly Dictionary<string, (string en, string tr)> Map = new()
    {
        // state
        ["state.playing"] = ("Playing", "Çalıyor"),
        ["state.paused"] = ("Paused", "Duraklatıldı"),
        ["state.noconn"] = ("No connection", "Bağlantı yok"),
        ["state.offline"] = ("{0} offline", "{0} çevrimdışı"),
        ["state.offline.short"] = ("offline", "çevrimdışı"),
        ["track.none"] = ("No track", "Parça yok"),

        // menus / tooltips
        ["menu.big"] = ("Big screen", "Büyük ekran"),
        ["menu.mode"] = ("Mini / full view", "Mini / tam görünüm"),
        ["menu.local"] = ("Listen on this PC", "PC'de dinle"),
        ["menu.local.long"] = ("Listen on this PC (same file from the NAS)", "PC'de dinle (NAS'tan aynı dosya)"),
        ["menu.settings"] = ("Settings…", "Ayarlar…"),
        ["menu.hide"] = ("Hide (reopen from the tray)", "Gizle (tepsiden geri açılır)"),
        ["menu.exit"] = ("Exit", "Çıkış"),
        ["menu.widget"] = ("Show / hide widget", "Widget göster / gizle"),
        ["menu.refresh"] = ("Refresh library", "Kütüphaneyi yenile"),
        ["menu.crawl.pause"] = ("Pause library indexing", "Dizin taramasını duraklat"),
        ["menu.crawl.resume"] = ("Resume library indexing", "Dizin taramasını sürdür"),
        ["menu.crawl.done"] = ("Index complete", "Dizin tamam"),
        ["tip.prev"] = ("Previous", "Önceki"),
        ["tip.playpause"] = ("Play / pause", "Çal / duraklat"),
        ["tip.next"] = ("Next", "Sonraki"),
        ["tip.search"] = ("Search (/)", "Ara (/)"),

        // big screen
        ["big.title"] = ("EversoloForPC — Now playing", "EversoloForPC — Şimdi çalıyor"),
        ["big.upnext"] = ("Up next", "Sırada"),
        ["big.hint"] = ("/ search     Space play / pause     ← → track     L listen on this PC     F5 refresh library     Esc close",
                        "/ ara     Boşluk çal / duraklat     ← → parça     L PC'de dinle     F5 kütüphaneyi yenile     Esc kapat"),
        ["search.placeholder"] = ("Search tracks, albums or artists…", "Parça, albüm veya sanatçı ara…"),

        // library / search status
        ["lib.loading"] = ("Loading library… ({0} albums)", "Kütüphane yükleniyor… ({0} albüm)"),
        ["lib.empty"] = ("Library is empty. Press F5 to refresh.", "Kütüphane boş. F5 ile yenile."),
        ["lib.done"] = ("{0} albums, {1} tracks indexed", "{0} albüm, {1} parça dizinde"),
        ["lib.upgrading"] = ("updating index (adding duration and folder info)", "dizin güncelleniyor (süre/klasör bilgisi ekleniyor)"),
        ["lib.building"] = ("building track index: {0} tracks", "parça dizini oluşturuluyor: {0} parça"),
        ["lib.line"] = ("{0} albums · {1} ({2}%){3}", "{0} albüm · {1} ({2}%){3}"),
        ["lib.stopped"] = (" · stopped", " · durdu"),
        ["search.count"] = ("{0} albums, {1} tracks{2}   ·   Enter: open / play", "{0} albüm, {1} parça{2}   ·   Enter: aç / çal"),
        ["search.indexpct"] = ("   ·   index {0}%", "   ·   dizin {0}%"),
        ["kind.album"] = ("Album", "Albüm"),
        ["kind.track"] = ("Track", "Parça"),
        ["kind.missing"] = ("missing", "yok"),
        ["back.title"] = ("Back", "Geri"),
        ["back.sub"] = ("Back to search results", "Arama sonuçlarına dön"),
        ["album.loading"] = ("Loading {0}…", "{0} yükleniyor…"),
        ["album.line"] = ("{0}  —  {1}   ·   {2} tracks   ·   Enter: play", "{0}  —  {1}   ·   {2} parça   ·   Enter: çal"),
        ["device.noanswer"] = ("The device did not answer. Try again.", "Cihaz cevap vermedi. Tekrar dene."),
        ["play.now"] = ("Playing: {0}", "Çalınıyor: {0}"),
        ["play.refused"] = ("The device refused the play request", "Cihaz çalma isteğini kabul etmedi"),
        ["missing.meta"] = ("Not found on the NAS (device database is stale) · ", "NAS'ta bulunamadı (cihaz veritabanı eski) · "),
        ["missing.select"] = ("This copy is not on the NAS; the device cannot play it. Pick another copy.",
                              "Bu kopya NAS'ta yok; cihaz çalamaz ve uyarı gösterir. Başka kopyayı seç."),
        ["fmt.tracks"] = ("{0} tracks", "{0} parça"),

        // local player
        ["lp.notrack"] = ("No track on the device", "Cihazda parça yok"),
        ["lp.streaming"] = ("Streaming ({0}) – PC playback not available", "Akış ({0}) – PC'de dinleme yok"),
        ["lp.nopath"] = ("File path unknown (waiting for the queue)", "Dosya yolu bilinmiyor (kuyruk bekleniyor)"),
        ["lp.missing"] = ("Not on the NAS: {0}", "NAS'ta dosya yok: {0}"),
        ["lp.opening"] = ("Opening: {0}", "Açılıyor: {0}"),
        ["lp.ended"] = ("PC: file ended", "PC: dosya bitti"),
        ["lp.ended.nudge"] = ("PC: file ended · sent 'next' to the device", "PC: dosya bitti · cihaza 'sonraki' gönderildi"),
        ["lp.ended.released"] = ("PC: file ended · audio device released", "PC: dosya bitti · ses cihazı serbest"),
        ["lp.paused.released"] = ("PC ⏸ audio device released", "PC ⏸ ses cihazı serbest"),
        ["lp.nodevice"] = ("No audio device found", "Ses cihazı bulunamadı"),
        ["lp.outfail"] = ("Could not open the audio output: {0}", "Ses çıkışı açılamadı: {0}"),
        ["lp.noffmpeg"] = ("ffmpeg not found (winget install Gyan.FFmpeg)", "ffmpeg bulunamadı (winget install Gyan.FFmpeg)"),
        ["lp.ffstart"] = ("Could not start ffmpeg", "ffmpeg başlatılamadı"),

        // settings window
        ["set.title"] = ("EversoloForPC settings", "EversoloForPC ayarları"),
        ["sec.general"] = ("General", "Genel"),
        ["lbl.lang"] = ("Language", "Dil"),
        ["sec.device"] = ("Device", "Cihaz"),
        ["lbl.ip"] = ("Streamer IP", "Cihaz IP"),
        ["btn.discover"] = ("Find on network", "Ağda bul"),
        ["lbl.poll"] = ("Refresh interval (ms)", "Yenileme aralığı (ms)"),
        ["sec.screens"] = ("Screens", "Ekranlar"),
        ["lbl.monitor"] = ("Big screen monitor", "Büyük ekran monitörü"),
        ["chk.bigstart"] = ("Show the big screen at startup (with 2+ monitors)", "Büyük ekranı açılışta göster (2+ monitör varsa)"),
        ["chk.autostart"] = ("Start with Windows", "Windows ile başlat"),
        ["chk.vinyl"] = ("Spinning record", "Dönen plak"),
        ["chk.kenburns"] = ("Background motion", "Arka plan hareketi"),
        ["chk.spectrum"] = ("Spectrum", "Spektrum"),
        ["sec.audio"] = ("Listen on this PC (audio chain)", "PC'de dinle (ses zinciri)"),
        ["lbl.outdev"] = ("Audio output device", "Ses çıkış cihazı"),
        ["chk.excl"] = ("WASAPI exclusive: bypass the Windows mixer and resampler (bit-perfect)",
                        "WASAPI exclusive: Windows karıştırıcısını/yeniden örnekleyicisini atla (bit-perfect)"),
        ["lbl.maxrate"] = ("DAC's highest PCM rate", "DAC'ın üst PCM hızı"),
        ["lbl.dsdgain"] = ("DSD gain (dB)", "DSD kazanç (dB)"),
        ["hint.dsdgain"] = ("SACD peaks can exceed 0 dBFS; −3 prevents clipping, 0 is loudest.",
                            "SACD tepeleri 0 dBFS'i aşabilir; −3 kırpmayı önler, 0 en gür."),
        ["lbl.dsdlp"] = ("DSD low-pass (Hz)", "DSD alçak geçiren (Hz)"),
        ["hint.dsdlp"] = ("Removes DSD's noise above 25 kHz (linear-phase FIR, inaudible in-band). 0 = off. Recommended 30000.",
                          "DSD'nin 25 kHz üstü gürültüsünü keser (linear-phase FIR, duyulur banda dokunmaz). 0 = kapalı. Önerilen 30000."),
        ["chk.limiter"] = ("−1 dBFS peak limiter on DSD (transparent; only touches peaks above it)",
                           "DSD'de −1 dBFS tepe sınırlayıcı (şeffaf; yalnız eşiği aşan tepelere dokunur)"),
        ["lbl.nas"] = ("NAS share root", "NAS paylaşım kökü"),
        ["hint.nas"] = ("Leave empty to detect it from the device (nfs://host/share becomes \\\\host\\share).",
                        "Boş bırakılırsa cihazdan algılanır (nfs://host/share, \\\\host\\share olur)."),
        ["lbl.ffmpeg"] = ("ffmpeg path", "ffmpeg yolu"),
        ["btn.cancel"] = ("Cancel", "İptal"),
        ["btn.save"] = ("Save", "Kaydet"),
        ["monitor.auto"] = ("Automatic (first non-primary monitor)", "Otomatik (ana olmayan ilk monitör)"),
        ["monitor.primary"] = (" (primary)", " (ana)"),
        ["dev.default"] = ("Windows default output", "Windows varsayılan çıkışı"),
        ["ff.notfound"] = ("ffmpeg not found: winget install Gyan.FFmpeg", "ffmpeg bulunamadı: winget install Gyan.FFmpeg"),
        ["ff.found"] = ("Found: {0}", "Bulundu: {0}"),
        ["st.nodevice"] = ("No streamer found. Enter its IP or search the network.", "Cihaz bulunamadı. IP gir veya ağda ara."),
        ["st.scanning"] = ("Scanning the network (port 9529)…", "Ağ taranıyor (port 9529)…"),
        ["st.found"] = ("Found: {0} ({1})", "Bulundu: {0} ({1})"),
        ["st.notfound"] = ("No Eversolo found on the network.", "Ağda Eversolo bulunamadı."),
        ["st.ipempty"] = ("The IP cannot be empty.", "IP boş olamaz."),
        ["st.gain"] = ("DSD gain must be between −24 and 0 dB.", "DSD kazanç −24 ile 0 dB arasında olmalı."),
        ["st.lp"] = ("DSD low-pass must be between 0 (off) and 100000 Hz.", "DSD alçak geçiren 0 (kapalı) ile 100000 Hz arasında olmalı."),
        ["st.verifying"] = ("Checking the device…", "Cihaz doğrulanıyor…"),
        ["st.noapi"] = ("No Eversolo API answer at {0}. Saved anyway.", "{0} adresinde Eversolo API cevap vermedi. Yine de kaydedildi."),
    };
}

/// <summary>XAML: Text="{local:Tr key}".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public string Key { get; set; }
    public TrExtension(string key) { Key = key; }
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
