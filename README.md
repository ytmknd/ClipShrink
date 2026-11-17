# ClipShrink

Trim the start and end of a video and export it as a smaller MP4.

ClipShrink comes in two editions that share the same workflow: a **web app** that runs entirely in your browser, and a **Windows desktop app** built on FFmpeg. Both editions show their interface in **Japanese** on Japanese systems and in **English** everywhere else (see [Language](#language)).

### ▶ Try the web app: **https://ytmknd.github.io/ClipShrink/**

No install and no account. Your video is never uploaded, because all processing happens on your device.

---

## Editions

| | Web app | Windows app |
|---|---|---|
| Where it runs | Any recent Chrome or Edge (desktop) | Windows 10/11 x64 |
| Engine | Browser [WebCodecs](https://developer.mozilla.org/docs/Web/API/WebCodecs_API) via [Mediabunny](https://mediabunny.dev/) | [FFmpeg](https://ffmpeg.org/) (libx264 / libx265) |
| Install | None, just open the link | Build from source (see below) |
| Privacy | Video stays in the browser | Video stays on the PC |
| Size control | Quality presets, or target size with automatic re-encode | CRF presets, or target size with 2-pass H.264 |
| Best for | Quick cuts on any PC | Best compression and full control |

## How to use

1. **Open a video.** Drag and drop it, or click the open button. MP4 is the main target; MOV, M4V, MKV and WebM also work, and so do iPhone screen recordings (HEVC).
2. **Set the range.**
   - Drag the start and end sliders, type a time (`90`, `1:30`, `1:30.5`), or nudge by ±0.1 s.
   - While the video plays, press **Set start here** or **Set end here**.
   - **Play range** plays only the selected part.
   - Still frames of the start and end points are shown beside the player.
3. **Choose how small.**
   - **By quality:** pick a preset from high quality to smallest.
   - **By target size:** enter a size in MB.
   - Optionally choose the codec (H.264 or H.265), downscale the resolution, cap the frame rate, or set the audio bitrate or remove the audio.
4. **Check before exporting.** **Compressed preview** encodes 5 seconds with the current settings, plays the result and estimates the final file size.
5. **Export.** A progress bar shows how far the export has got, and you can cancel at any time. When it finishes, ClipShrink shows the output size next to the original's.

## Language

ClipShrink detects the language automatically. It shows Japanese when the system or browser language is Japanese, and English for every other language.

| | How the language is chosen | How to force a language |
|---|---|---|
| Web app | The browser's preferred language (`navigator.languages`) | Add `?lang=ja` or `?lang=en` to the URL, for example https://ytmknd.github.io/ClipShrink/?lang=en |
| Windows app | The Windows display language (`CultureInfo.CurrentUICulture`) | Set the environment variable `CLIPSHRINK_LANG=ja` or `CLIPSHRINK_LANG=en` before starting the app |

Where the strings live:

- **Web app:** all strings are in [`docs/i18n.js`](docs/i18n.js), with one dictionary per language. Text in `index.html` is marked with `data-i18n` attributes, and `app.js` looks up its messages with `t('key', { vars })`.
- **Windows app:** each message is written next to its translation as `L.T("日本語", "English")`. The helper is [`src/L.cs`](src/L.cs). It also switches the UI font: Yu Gothic UI for Japanese, Segoe UI for English.

To add a language to the web app, add a new dictionary to `docs/i18n.js` and extend the language check at the top of that file.

---

## Web app

The web app is a static page in [`docs/`](docs/), made of HTML, CSS and plain JavaScript modules with no build step. GitHub Pages serves it from the `main` branch's `/docs` folder.

- **Private by design.** Decoding and encoding use the browser's WebCodecs API, usually on the GPU. Nothing is sent to a server. This is also much faster than ffmpeg.wasm.
- **Large files.** In Chrome and Edge, the output streams straight to the file you pick through the File System Access API, so multi-GB videos do not have to fit in memory. Other browsers build the file in memory and download it.
- **Thumbnails for any decodable format.** The start and end frames are decoded with WebCodecs, so they appear even when the `<video>` element cannot play the file.
- **Reliable target size.** Target-size mode encodes at a constant bitrate. If the result is more than 5% over the target, the app adjusts the bitrate and encodes again, up to three times in total.
- **Encoder-safe output.** Width and height are always rounded to even numbers. The resolution presets refer to the **short side**, so portrait phone recordings scale sensibly.

**Requirements:** a recent desktop Chrome or Edge. HEVC input, such as iPhone recordings, needs a browser and GPU that can decode HEVC. On Windows this usually means a GPU from the last several years. Other browsers may work but have not been tested.

**Run it locally.** ES modules do not load from `file://`, so serve the folder over HTTP:

```bash
npx serve docs          # or: python -m http.server -d docs
```

Mediabunny 1.65.1 is vendored in `docs/vendor/`, so the page loads no third-party CDN.

---

## Windows app

A WinForms app (C#, .NET 10) that drives a bundled `ffmpeg.exe`.

- **Built-in player.** The player uses Edge WebView2, plays audio and supports range playback. Moving a trim slider also seeks the player.
- **Automatic preview proxy.** Some videos cannot be played by the player, for example HEVC on a PC without the HEVC Video Extensions. For these, ClipShrink makes a lightweight H.264 copy that is used only for preview. The copy keeps the original timestamps, so trim points still match. Export always uses the original file.
- **Compression options:**
  - CRF presets from 23 (high quality) to 35 (smallest).
  - Target size with 2-pass H.264.
  - Encoder speed presets.
  - Downscaling to 1080p, 720p, 540p, 480p or 360p, and a frame-rate cap.
- **Frame-accurate cuts.** ClipShrink seeks with input-side `-ss` and limits the length with `-t`, then fully re-encodes. Output files use `yuv420p` and `+faststart`, so they play widely.
- **Safe export:**
  - ClipShrink asks before overwriting a file.
  - Cancelling deletes the partial file.
  - If the output turns out larger than the input, ClipShrink warns you.

**Requirements:**
- Windows 10 or 11 (x64).
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) for the player. It is preinstalled on Windows 11.
- For the framework-dependent build, the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).

### Building

1. Install the .NET 10 SDK.
2. Place an `ffmpeg.exe` at `tools\ffmpeg.exe`, for example a full build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/). It is about 220 MB, so it is not stored in this repository.
3. Build with one of these commands:

```bat
build.bat            :: framework-dependent build  -> app\
build.bat portable   :: self-contained single file -> portable\
```

Either way, ship the whole output folder: `ClipShrink.exe` reads `ffmpeg.exe` from the folder it is in. The project uses NTFS hard links where it can, so the build and publish folders do not each hold a full copy of FFmpeg on disk.

---

## Project structure

```
docs/                    Web app (GitHub Pages root)
  index.html  style.css  app.js
  i18n.js                UI strings (Japanese / English) and language detection
  vendor/                Mediabunny bundle and its MPL-2.0 license
src/                     Windows app
  MainForm.cs            UI, trimming, preview proxy, export flow
  VideoPlayer.cs         WebView2 player (range playback, local file serving with HTTP Range)
  EncodeSettings.cs      Encode options and FFmpeg argument builder
  FFmpeg.cs              Probing (parses `ffmpeg -i`), frame grabbing, progress reporting
  L.cs                   UI language selection (Japanese / English)
  Program.cs             Entry point
tools/ffmpeg.exe         FFmpeg binary for the Windows app (not tracked in git)
ClipShrink.csproj  build.bat
LICENSE  THIRD_PARTY_NOTICES.txt
```

## License

ClipShrink's own source code is released under the [MIT License](LICENSE).

Third-party components keep their own licenses:

- **FFmpeg** (`ffmpeg.exe`, Windows app only) is licensed under **GPL v3**. The build used here is configured with `--enable-gpl --enable-version3`. ClipShrink runs it as a separate process and does not modify it. If you redistribute `ffmpeg.exe`, follow the GPL v3 terms, which include stating where its source can be obtained. See [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
- **Mediabunny** (web app) is licensed under the **Mozilla Public License 2.0**. The license text is in [`docs/vendor/LICENSE-mediabunny.txt`](docs/vendor/LICENSE-mediabunny.txt), and the vendored file is unmodified.
- **Microsoft Edge WebView2 SDK** (Windows app) has its own license terms. See the [NuGet package page](https://www.nuget.org/packages/Microsoft.Web.WebView2).
