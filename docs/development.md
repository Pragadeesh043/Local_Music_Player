# Development

MP4A is a Windows x64 desktop player. Open `MP4A.slnx` in your IDE. The [README](../README.md) covers installation and playback prerequisites.

| Area | What it contains |
| --- | --- |
| `src/MP4A.Desktop/UI` | WinUI window, WebView2 bridge, media controls and library workflows |
| `src/MP4A.Desktop/Audio` | Decoding, DSP, analysis, buffering and hardware output |
| `src/MP4A.Desktop/Library` | Local catalog, indexing, artwork storage and music formats |
| `src/MP4A.Desktop/Native` | Bundled Steam Audio runtime and upstream notices |
| `src/MP4A.Web` | React UI, typed bridge and browser demo fixtures |
| `examples/native-host` | Optional C++ WebView2 example using a simulated engine |
| `packaging` | NSIS installer and dependency notice staging |

The desktop host loads the built web UI into WebView2. The UI sends typed requests for catalog pages, playback, and settings. The host publishes playback state and signal telemetry. The audio engine feeds WASAPI or ASIO independently of the web UI.

## Build on Windows

Install the .NET 10 SDK and Node.js 22.12+ or a supported newer version. Install NSIS for installer builds. At runtime the app uses the Windows WebView2 Evergreen Runtime.

```powershell
.\build.ps1
.\build-app.ps1
```

The development build lands in `artifacts\Release`. The installer lands at `artifacts\MP4A-Setup-x64.exe`. Use `-Configuration Debug` on `build.ps1` for a debug build or `-NoRestore` when dependencies are already restored. The scripts can use local SDK and NSIS installations under `.dotnet` and `.tools\nsis`.

Place both Windows x64 FFmpeg executables in `.tools\ffmpeg`, pass `-FFmpegDirectory` to `build.ps1`, or set the `FFmpegDirectory` MSBuild property. The desktop project copies the pair, adjacent DLLs, and upstream notices to `Tools\FFmpeg` in its build and publish outputs. Dev builds can run without a bundle. For a portable copy, keep `Tools\FFmpeg` beside `MP4A.exe`.

At runtime MP4A checks `Tools\FFmpeg` beside the running app, the folder saved in FFmpeg setup, `%LOCALAPPDATA%\MP4A\Tools\FFmpeg`, and then absolute directories on `PATH` in order. The first directory containing both executables supplies the pair. In NSIS and MSIX installs the packaged pair takes precedence.

Run `./packaging/Test-FFmpeg.ps1 -Directory <ffmpeg-folder>` to check the tools and release decoder requirements. When playback fails, MP4A shows a short message in the player and writes the FFmpeg executable, exit code, and stderr to `Logs\Soundroom.log` in the active profile. Press Next to continue from a failed track.

Optionally put `sacd_extract.exe` in `.tools\sacd` for development. See the [setup guide](../README.md) and [release checklist](release.md).

For UI work, run `npm.cmd ci` and `npm.cmd run dev` from `src/MP4A.Web`. The browser demo uses the bundled fixtures and synthesized audio. See the [C++ example](../examples/native-host/README.md) for a second way to exercise the bridge.

## Local data and compatibility

The default profile remains `%LOCALAPPDATA%\Soundroom`. Use an absolute `MP4A_DATA_DIRECTORY` for a test profile. WebView2 state lives in the profile's `WebView2` folder. The historical `Soundroom` namespace, deployed `Glassroom` UI folder, and local WebView2 origins are internal names retained for compatibility.

| Database | Tables |
| --- | --- |
| `library.db` | `tracks` holds track records. `track_catalog` and `track_sort` index browsing and sorting. `settings` and `presets` hold user preferences. |
| `ArtworkThumbnails/artwork.db` | `thumbnails` caches extraction results. `cover_assets` stores deduplicated cover images and previews. `cover_links` associates track keys with saved covers. |

Keep changes focused and describe verification. Use an isolated profile and a synthetic fixture for playback bug reports. Redact paths and track names from logs. See the [release checklist](release.md) and [third-party notices](../THIRD-PARTY-NOTICES.md).
