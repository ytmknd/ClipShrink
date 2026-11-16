# ClipShrink

ClipShrink is a small Windows desktop app for trimming the start and end of a video and exporting it as a smaller MP4. It is written in C# (WinForms, .NET 10) and uses [FFmpeg](https://ffmpeg.org/) for all decoding and encoding.

The user interface is in Japanese.

## Features

- **Open videos** with the button or by drag and drop. MP4 is the main target, and MOV, M4V, MKV, WebM and AVI also work. You can also pass a file path as the first command-line argument.
- **Trim** the start and end with sliders (0.1 s steps), typed timestamps (`90`, `1:30`, `1:30.5`, `0:01:30.25`) or ±0.1 s buttons. Still frames of the start and end positions are shown beside the player.
- **Built-in player** (Edge WebView2) with audio:
  - **Play range** plays from the start point and stops at the end point.
  - **Set start/end here** takes the current playback position.
  - Moving a trim slider also seeks the player.
- **Automatic preview proxy.** If the player cannot decode the source, for example HEVC iPhone screen recordings on a PC without the HEVC Video Extensions, ClipShrink makes a lightweight H.264 copy for preview only. The copy keeps the original timestamps, so trim points still match. Export always uses the original file.
- **Compressed preview.** ClipShrink encodes 5 seconds from the start point with the current settings, plays the result and estimates the size of the whole clip.
- **Two ways to control size:**
  - *Quality* uses CRF presets from 23 (high quality) to 35 (smallest).
  - *Target size* takes a size in MB and works out the bitrate from the trimmed length. H.264 uses two-pass encoding for accuracy.
- **Other options:** H.264 (libx264) or H.265 (libx265), downscale to 1080p, 720p, 540p, 480p or 360p, cap the frame rate at 30, 24 or 15 fps, set the AAC audio bitrate or remove audio, and choose the encoder speed preset.
- **Export:**
  - A progress bar shows how far the export has got, and you can cancel. Cancelling deletes the partial file.
  - ClipShrink asks before overwriting an existing file.
  - When the export finishes, it compares the output size with the input and warns you if the file got bigger.
  - Output files use `yuv420p` and `+faststart` so they play widely.

## Requirements

- Windows 10 or 11 (x64)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). It is preinstalled on Windows 11. Without it, preview playback is disabled, but trimming and export still work.
- `ffmpeg.exe`, which the release folders include
- The framework-dependent build also needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). The portable build does not.

## Download / distribution layout

There are two publish flavours. Ship the whole folder in either case.

| Folder | Contents | Needs .NET installed |
|---|---|---|
| `portable\` | `ClipShrink.exe` (single self-contained file), `ffmpeg.exe`, notices | No |
| `app\` | `ClipShrink.exe` with its DLLs, `ffmpeg.exe`, notices | Yes (.NET 10 Desktop Runtime) |

ClipShrink looks for `ffmpeg.exe` in two places, in this order:

1. The folder that contains `ClipShrink.exe` (the bundled copy)
2. `D:\WebMtoMP4Converter\ffmpeg.exe` (a development fallback)

If neither exists, ClipShrink shows an error at startup.

## Building

You need the .NET 10 SDK. FFmpeg is not checked into source control, so place an `ffmpeg.exe` (for example a full build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/)) at:

```
tools\ffmpeg.exe
```

Then run one of these:

```bat
build.bat            :: framework-dependent build -> app\
build.bat portable   :: self-contained single-file build -> portable\
```

To run the commands yourself:

```bat
dotnet publish ClipShrink.csproj -c Release -r win-x64 --self-contained false -o app
dotnet publish ClipShrink.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o portable
```

`ffmpeg.exe` is about 220 MB, so it is copied next to the executable instead of being embedded in the single-file EXE. The project tells MSBuild to use NTFS hard links when it can, so the build and publish folders do not each hold a full copy on disk.

## Project structure

```
ClipShrink.csproj
build.bat
src/
  Program.cs          Entry point
  MainForm.cs         UI, trimming, preview proxy, export flow
  VideoPlayer.cs      WebView2-based player (HTML5 <video>, range playback,
                      local file serving with HTTP Range support)
  EncodeSettings.cs   Encode options and FFmpeg argument builder
  FFmpeg.cs           Media probing (parses `ffmpeg -i`), frame grabbing,
                      process execution with progress reporting
tools/
  ffmpeg.exe          Bundled FFmpeg binary (not part of the source license)
THIRD_PARTY_NOTICES.txt
LICENSE
```

## How it works (briefly)

- **Probing:** ClipShrink runs `ffmpeg -i` and parses its output for duration, resolution, frame rate, codecs and audio, so `ffprobe` is not needed.
- **Trimming:** input-side `-ss <start>` and `-t <length>`, then a full re-encode, so cuts are frame-accurate.
- **Progress:** read from `-progress pipe:1` (`out_time_us`).
- **Player:**
  - A local HTML page is mapped to a virtual host in WebView2.
  - The source video is served through `WebResourceRequested` with byte-range support, which makes seeking work.
  - The temporary proxy and sample files go to `%TEMP%\ClipShrink_preview` and are deleted on exit and on the next start.

## License

ClipShrink's source code is released under the [MIT License](LICENSE).

### Third-party components

- **FFmpeg** (`ffmpeg.exe`) is **not** covered by the MIT License. The bundled build is licensed under the **GNU GPL v3** (built with `--enable-gpl --enable-version3`). ClipShrink only runs it as a separate process and does not modify it. If you redistribute `ffmpeg.exe`, you must follow the GPL v3 terms, including saying where its source code can be obtained. See [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).
- **Microsoft Edge WebView2 SDK** (`Microsoft.Web.WebView2` NuGet package) is distributed under its own license terms. See the [package page](https://www.nuget.org/packages/Microsoft.Web.WebView2).
