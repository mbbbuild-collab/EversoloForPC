# Eversolo / Zidoo local control API — field notes

Observed on a DMP-A8 (firmware v1.5.75, Android 11). Base: `http://<device-ip>:9529/`.
No authentication. Everything here was found by experiment and by reading
[wizmo2/zidoo-player](https://github.com/wizmo2/zidoo-player); none of it is official.

## Music control (`ZidooMusicControl/v2/`)

| Endpoint | Notes |
|---|---|
| `getState` | `state`: **3 = playing, 4 = paused**. `playingMusic`, `position`, `duration` (ms), `volumeData`, `playType`. |
| `getPlayQueue?start=0&count=N` | Queue items carry `uri` (path relative to the share root), `path`, `fileName`, `fileSize`, `duration`, `SampleRate`, `bits`. |
| `getImage?id=<trackId>&music_type=<type>&target=16` | JPEG cover. Works with a **track** id only. Returns a generic note icon when the device has no art. |
| `getAlbums?start=&count=` | All albums: `id`, `name`, `artist`, `pubDate`. |
| `getAlbumMusics?id=<albumId>&start=&count=` | Tracks of an album. Sample rate is `SampleRate` (number) here, `sampleRate` (string) in `getState`. |
| `getFolders?start=&count=` | Music sources, e.g. `url: nfs://<nas>/<share>?...`, total track `count`. |
| `searchMusic` / `searchAlbum` / `searchArtist` `?key=` | Device-side search; inconsistent (prefix / pinyin-like). Index locally instead. |
| `playMusic?type=4&id=<albumId>&musicId=<trackId>&music_type=0&trackIndex=0&sort=0` | Loads the album into the queue and starts at that track. `type`: 3 artist, 4 album, 5 playlist. Wrong parameters play something arbitrary. |
| `playMusics?ids=<id,id>&musicId=<id>&trackIndex=-1` | Queue the given tracks. |
| `playOrPause`, `playNext`, `playLast` | **Act even without parameters** — never call them as an "existence test". |

There is no bulk "all tracks" endpoint: a full index means one `getAlbumMusics` per album.

### Streaming services (Qobuz, Tidal, radio…)

`playingMusic` carries `streamId` ("qobuz"), `streamQuality`, `audioQuality`, `albumArt` / `albumArtBig`
(cover URL), `streamUrl`, `trackUrl`, `type 3`, negative ids. `sampleRate` / `bitrate` / `bits` are **0 for the
first seconds** and fill in later, so re-read them on every poll. Queue items have an `https://…` `uri`
(signed, time-limited); there is no file to mirror, and `getImage` returns the generic icon — use `albumArt`.

## Remote control keys (`ZidooControlCenter/RemoteControl/sendkey?key=`)

`Key.MediaPlay`, `Key.MediaPause`, `Key.MediaNext`, `Key.MediaPrevious`, … always work, including when
the music API's state is frozen (below).

`ZidooControlCenter/getModel` returns model / firmware; network discovery uses it.

## Traps

- **The music database stalls easily.** Unknown or empty-answer endpoints (`getSongs`,
  `getArtistAlbums`, `playMusic?type=2/3/4` with a track id, …) start heavy queries on large libraries
  and hang every library endpoint for minutes; a request that times out on the client keeps running
  on the device. Do not probe blindly. When library calls start timing out, go completely quiet for
  a few minutes. `getState` / `getPlayQueue` keep working meanwhile.
- **Frozen state.** In some playback modes (`playType 5`, seen with DSF) `getState` reports
  `state 3, position 0, duration 0` forever, `playOrPause` changes nothing visible and the device may
  sit on a finished track. Workaround used here: keep your own play/pause flag, send remote keys, take
  the duration from the file (ffprobe), and press `Key.MediaNext` when the file is over and the device
  has not moved on.
- **Stale position on track change.** For a poll or two after a change `position` still belongs to the
  previous track. Start the new track at 0 and only trust drift after ~3 s.
- **Stale library.** The database keeps entries (and old folder names) for files that were moved or
  deleted on the NAS; playing one shows a "kind reminder" dialog on the device. Check the path on the
  share; match file names loosely (`’` vs `'`, `–` vs `-`, case, accents) and look in sibling folders.
- One album id may fail consistently; record it as empty after a few fast failures or the crawl never ends.
- Crawl gently: one request at a time with a pause. Two eager workers made the device's own screen
  and the phone app's screen mirror stutter.

## DSD to PCM (PC playback)

ffmpeg decodes DSD to float PCM at 1/8 of the DSD rate. Peaks can exceed 0 dBFS (SACD allows about
+3 dB), so writing integers without headroom **clips** — heard as crackle on loud passages. Chain used:
linear-phase FIR low-pass at 30 kHz (`firequalizer`, `accuracy=250`; the IIR `lowpass` filter only has
1–2 poles and audibly rolls off the top octave) → gain (default −3 dB) → `alimiter` at −1 dBFS → soxr
resample to the DAC's highest rate in the same family (88.2 / 96 kHz for a 24/96 DAC).

WPF `MediaPlayer` (Media Foundation) and LibVLC 3 cannot decode DSD at all.
