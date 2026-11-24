# Development and releases

## Build on Windows

Install the .NET 10 SDK and Node.js 22.12+ or a supported newer version. Install NSIS for the installer build. MP4A requires the Windows WebView2 Evergreen Runtime at launch.

```powershell
.\build.ps1
.\build-app.ps1
```

The first command creates a development build in `artifacts\Release`. Run `MP4A.exe` alongside the other files in that directory. The second builds the self-contained installer at `artifacts\MP4A-Setup-x64.exe`. Run `./build.ps1 -Configuration Debug` for Debug or use `-NoRestore` after the dependencies for that configuration have been restored. The scripts use local SDK and NSIS installations under `.dotnet` and `.tools\nsis` when available.

For local development, put FFmpeg and ffprobe in `.tools\ffmpeg` and optionally put sacd_extract in `.tools\sacd`. Development builds copy available helpers alongside the app. Public installer builds leave their installation to the user. See the [setup guide](../README.md) for upstream downloads.

Use an absolute `MP4A_DATA_DIRECTORY` to test with a separate library, settings, and caches. Open `Soundroom.slnx` in your IDE.

## Repository layout

| Path | Role |
| --- | --- |
| `src/Soundroom/UI` | WinUI host, WebView2 bridge, window behavior, and Windows media controls |
| `src/Soundroom/Audio` | Audio decoding, DSP, playback, device routing, and analysis |
| `src/Soundroom/Library` | SQLite catalog, file watching, artwork caching, and format imports |
| `src/Soundroom/Native` | Steam Audio dependency and upstream notices |
| `src/Glassroom/ui` | React and TypeScript frontend shared by MP4A and the browser demo |
| `src/Glassroom/native` | Optional C++ demo host with its own simulated engine |
| `packaging` | NSIS installer and dependency notice staging |

See [Glassroom](../src/Glassroom/README.md) to run the frontend demo or the optional C++ host. The Windows installer uses the WinUI host in `src/Soundroom`.

## Contributing and publishing

Keep changes focused and include how you verified them. For playback issues, include the audio format, sample rate, output route, and whether the issue reproduces with a short synthetic fixture. Share only redacted logs. Never include private libraries, decoded audio, personal paths, or caches.

Use a GitHub Release asset for the installer. Git ignores build output, local SDKs, package caches, and test profiles. Before publishing, review the history you intend to expose. Older commits contain a SACD helper binary that has since moved out of the source tree. A public source snapshot with fresh history excludes that older binary.

The original MP4A source is MIT licensed. Third-party terms are listed in [Third-party notices](../THIRD-PARTY-NOTICES.md).
