# MP4A

MP4A is a local music player for Windows 11 x64. It combines a WinUI 3 desktop host and a WebView2 library with a native audio engine built for headphone listening, DSP, and inspecting the signal sent to your audio device.

## Get started

1. Run `MP4A-Setup-x64.exe` from the release assets. A local installer build is written to `artifacts/MP4A-Setup-x64.exe`.
2. Install **FFmpeg for Windows x64** using one of the builds linked by the [FFmpeg project](https://ffmpeg.org/download.html#build-windows). Put both `ffmpeg.exe` and `ffprobe.exe` in the installed app’s `Tools\FFmpeg` folder or on PATH. Restart MP4A after changing PATH.
3. Open MP4A and choose **Watch folder** for an existing music collection or **Add files** for individual tracks. If MP4A prompts for WebView2, install the [Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download-section) and select **Reload Glassroom**.

For SACD ISO imports, obtain the upstream Windows x64 [sacd_extract helper](https://github.com/EuFlo/sacd-ripper/releases/tag/0.3.9.3b) and place `sacd_extract.exe` in the installed app’s `Tools\SACD` folder or on PATH. The installer bundles the .NET and Windows App SDK runtimes.

## Listening and library

- Choose a Windows output and route in **Output settings**. MP4A supports shared output, WASAPI exclusive PCM, binaural headphone playback, and device-dependent DoP and native DSD via ASIO.
- Use **DSP** for EQ, crossfeed, FIR convolution, resampling, normalization, and limiting. **Inspect signal** shows the active processing, meters, and device format.
- Browse your SQLite library by album or folder, search the full collection, and use smart playlists and shuffle. Gapless playback applies across compatible tracks. Windows Now Playing exposes transport controls and track metadata.
- Watched folders keep their library entries, ratings, and play counts while disconnected. **Reindex library** refreshes connected folders and hides files that have been deleted.
- Press **Ctrl+K** to focus search and **F11** for full screen. Launch with `--acrylic` to use Desktop Acrylic in place of Mica.

MP4A plays files where they are and stores settings, its SQLite catalog, artwork thumbnails, and logs in `%LOCALAPPDATA%\Soundroom`. Playback and indexing run locally. Before sharing logs, remove personal folder paths and track names.

## Project

- [Build and contribute](docs/development.md)
- [Audio modes, DSP, and verification](docs/audio.md)
- [Third-party notices](THIRD-PARTY-NOTICES.md)

MP4A’s original source and documentation are available under the [MIT license](LICENSE). Third-party components retain their own licenses.
