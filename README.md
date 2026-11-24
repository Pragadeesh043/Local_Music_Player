# Soundroom

Soundroom is an offline Windows 11 x64 music player with headphone surround, configurable DSP, and a live multichannel audio inspector.

Run `artifacts\MP4A-Setup-x64.exe` to install the release. A local development build runs from `artifacts\Release\MP4A.exe` with the other files in that directory. The app looks for FFmpeg and ffprobe under `Tools\FFmpeg` first. The library and profiles live in `%LOCALAPPDATA%\Soundroom`.

```powershell
.\build.ps1
```

Open `Soundroom.slnx` in your IDE. The build uses a workspace-local SDK when available and falls back to a system .NET 10 SDK. Use `.\build.ps1 -Configuration Debug` for a debug build, or add `-NoRestore` when packages are already restored for this project.

Run `.\build.ps1 -Installer` to build `artifacts\MP4A-Setup-x64.exe`. This requires the local FFmpeg tools and NSIS, either on `PATH` or under `.tools\nsis\nsis-3.11`. The setup EXE unpacks MP4A and its .NET and Windows App SDK runtimes into the folder you choose.

When you are ready to package your development changes, run `.\build-app.ps1` from the project directory. It builds the same Release installer. MP4A opens in a normal window with the taskbar visible. Press F11 for full screen.

For a project-local setup, place standalone `ffmpeg.exe` and `ffprobe.exe` in `.tools\ffmpeg`. The build copies them alongside the app. The workspace-local SDK is in `.dotnet`, NuGet packages are in `.nuget\packages`, and optional Python verification tools use `.venv`. Runtime library data and logs are stored per user.

## Removable music folders

Select **Reindex library** to rescan connected watched folders and hide deleted files, including tracks added through **Add files**. Tracks on disconnected drives keep their entries.

Plug in the drive and select **Watch folder** once. Choose the folder holding your collection or the drive root. Soundroom indexes metadata as it reads the existing files and plays music from the source. Later scans use file size and modification time to identify changes. Select **Add files** to import a disc image explicitly.

**Library tools** shows which registered folders are connected. A disconnected source keeps its indexed tracks and ratings and displays an offline message if selected for playback. Reconnect the drive and press Play to resume an interrupted track. Windows volume identity keeps a registered folder recognizable when the drive letter changes.

## Browsing large libraries

Search, album and folder filters, and smart-playlist filters apply to the entire SQLite catalog. Glassroom loads 60 results at a time as you scroll and renders only the rows near the viewport. Playback can advance across those pages.

Use **Library tools** to choose the sort order and direction before paging. Filename sorting treats numbers naturally. Album sorting keeps each edition in disc and track order. Date Added records the first time Soundroom indexes a track under this version. Older entries with no recorded import date sort together.

Turn on **Shuffle** next to the transport controls and select a track to start a shuffled queue in the current view. Tracks outside the current page can play, and disconnected sources are skipped. **Previous** retraces the tracks heard in that shuffle. Selecting a track directly starts a new shuffle. Soundroom saves the sort and shuffle settings.

Soundroom builds the browse index the first time it opens an existing library after this update. Track metadata and file contents are preserved. On a 100,000-track fixture, the migration took about nine seconds. Opening the indexed database and retrieving its first page took 332 ms, and a substring search took 178 ms. These timings measure database work and exclude desktop startup and rendering.

## Workspace

### Glassroom desktop interface

The React frontend lives in `src/Glassroom/ui`. MP4A uses WinUI 3 for the window, WebView2 lifecycle, and Windows file pickers. Device profiles, playback, indexing, and saved settings use the existing C# engine and SQLite database.

- **Output settings** in the top toolbar selects the endpoint and playback mode and saves per-device volume, resampling, RAM, DoP, and ASIO options.
- **Library tools** controls folders, sorting, smart filters, and saved playlists. Add files, Watch folder, and Reindex library are in the sidebar.
- **DSP** in the top toolbar edits parametric and graphic EQ, stereo processing, loudness, limiting, dither, FIR convolution, and presets. **Inspect signal** shows live meters and the applied signal path and hardware readback.
- **Track details** is available beside songs and in expanded Now Playing. It includes metadata, rating, track and edition analysis, artwork, and paged PDF booklets. Now Playing reads embedded timed lyrics or a matching `.lrc` file for individual tracks.

Build with Node.js 22.12+ and the .NET SDK using `./build.ps1`. To build alongside a running copy, use `./build.ps1 -OutputDirectory artifacts/Glassroom`. Use `./build-app.ps1` for the x64 installer. MP4A uses the Windows WebView2 Evergreen Runtime and offers a reload action if rendering fails. Mica is the default backdrop. Launch `MP4A.exe --acrylic` to use Desktop Acrylic.

Set `MP4A_DATA_DIRECTORY` to an absolute folder before launching to test with a separate library and settings.

| Location | Contents |
| --- | --- |
| `src/Soundroom/UI` | WinUI window host, playback orchestration, and desktop bridge |
| `src/Glassroom/ui` | Glassroom player, library, settings, and inspectors |
| `src/Soundroom/Audio` | Playback and output drivers, decoding, DSP, and analysis |
| `src/Soundroom/Library` | SQLite, metadata, CUE and SACD imports, and EQ presets |
| `src/Soundroom/Infrastructure` | Application diagnostics |
| `src/Soundroom/Native` | Runtime dependencies and upstream notices |
| `artifacts` | Runnable builds, grouped by configuration |

Playback and import errors appear in the status bar. Technical details are recorded in `%LOCALAPPDATA%\Soundroom\Logs\Soundroom.log`, with one rotated log retained. The log may contain local track names and paths.

## Audio buffering

A playback worker handles decoder reads, DSP, and meter calculation. It feeds a preallocated single-producer, single-consumer ring buffer. The output provider consumes queued blocks and advances the track position as it delivers frames. Each session buffers up to approximately half a second of audio, with a 16 MiB cap on queued audio data.

The worker measures each PCM block and groups its peak and RMS readings into display updates at up to 30 Hz. Supported CPUs use SIMD for the true-peak FIR and shared-output float packing. Watched-folder scans check the playback reserve before reading more directory entries or probing another file. When playback falls below half of its buffer capacity, the scan waits for the reserve to recover.

In **Output settings**, enable **Decode tracks to RAM** and set the total limit from 16 to 1,024 MiB. The current and next tracks share that budget, which defaults to 256 MiB. A track that cannot fit streams from its original location.

Live meters, the FFT, and the phase scope run while the live signal view is open and the window is visible. Playback and audio processing continue when the view closes or the player is covered.

If the ring runs dry, processed playback reports the underrun and supplies silence until audio is available. Direct PCM and DSD stop and report the underrun to preserve the integrity of the stream.

If the next track cannot finish preloading within the available audio buffer, Soundroom finishes delivering the current track and opens the next track separately. Changing the queue order during playback discards superseded audio at the next track boundary.

## Artwork

Now Playing and the library share thumbnails from embedded covers or folder artwork. SQLite stores up to 64 MiB of thumbnails in `%LOCALAPPDATA%\Soundroom\ArtworkThumbnails\artwork.db`. New thumbnails use WebP, and existing PNG entries remain reusable. The database records confirmed missing embedded covers separately from the image budget. Glassroom keeps recently used covers visible while checking for updates. Changes to file size or modification time invalidate thumbnails when the app next checks the source. Full-size artwork and booklets are available under **Track details**.

## Output modes

- **Binaural headphones** renders supported multichannel layouts with double-precision convolution using impulse responses generated by Steam Audio’s default HRTF. Select the stereo output connected to your IEMs.
  Discrete 5.1.4 and 7.1.4 PCM with a declared speaker layout render the four height channels as overhead virtual speakers.
  The HRTF response continues across compatible gapless tracks and finishes when the playback stream ends.
- **Stereo downmix** folds the source to two channels and applies the headphone DSP chain.
- **7.1 device / HeSuVi** sends mapped channels to an eight-channel Windows endpoint. It leaves spatial processing and volume to the external chain.
- **Exclusive · direct PCM** negotiates a WASAPI format at the track’s rate and preserves the decoded integer samples. It rejects devices that lack a compatible format. The inspector shows the valid bits and any wider transport container.
- **Exclusive · DSP / resampling** applies processing in double precision, uses the requested PCM rate and precision, then quantizes to the negotiated output format.
- **Exclusive · DSD over PCM** sends native DSF or uncompressed DSDIFF payloads in a DoP carrier. Enable the DoP capability for the selected DAC in **Output settings**.
- **ASIO · native DSD** opens the selected ASIO driver in one-bit DSD mode. It requires a 64-bit driver with native packed DSD buffers, the source’s clock rate, and sufficient output channels.

Select **Inspect signal** to open the signal inspector and expand individual stages. It shows the format the driver accepted, the controls associated with delivered audio blocks, and the routes that preserve PCM samples or the DSD payload. The view also shows when a control change is still queued. **Hardware readback** displays endpoint-reported hardware volume and mute where the driver exposes those controls. Native DSD over ASIO displays the driver-reported sample rate. Physical DAC clock and DoP lock readback require device-specific support. Use **Output settings** to save the route, DSP, resampler, RAM option, and volume mode for the output device.

## Feature coverage

| # | Feature | Implementation |
| --- | --- | --- |
| 1 | Bit-perfect playback | Strict WASAPI Exclusive integer bypass. Fixed digital gain, DSP and sample-rate conversion bypassed. |
| 2 | Automatic sample-rate and bit-depth switching | Per-track negotiation, with transparent padding to a supported wider word where required. |
| 3 | Lossless PCM | FLAC, ALAC, PCM WAV, AIFF/AIFC and WavPack through FFmpeg. |
| 4 | DSD and SACD | DSF and DSDIFF with PCM fallback, DoP and native ASIO routes for uncompressed DSD. The supported rate depends on the selected device and driver. SACD ISO import uses the packaged extractor for stereo and multichannel areas and caches extracted DSF tracks locally. DST-compressed standalone DFF uses the PCM route. |
| 5 | Lossy and legacy | Opus, Ogg Vorbis, AAC, MP3 and Musepack SV7/SV8. CUE sheets appear as individually playable tracks linked to the original source file. |
| 6 | 64-bit floating-point pipeline | Double-precision decoding, mix, volume, EQ, FIR, binaural rendering and PCM quantization. Steam Audio generates the HRTF coefficients in 32-bit float. Shared WASAPI uses a 32-bit float stream. |
| 7 | Gapless playback | A prefetched decoder joins compatible neighboring tracks in one WASAPI stream. A device format or speaker-layout change can reopen the stream. |
| 8 | RAM playback | Preload the current and next tracks within a shared, configurable RAM limit. |
| 9 | Digital and hardware volume | Digital attenuation, fixed output, and endpoint hardware volume when the Windows driver advertises it. The signal inspector shows the reported hardware level, range and step and offers hardware mute on supported endpoints. |
| 10 | Parametric EQ | Up to 64 bands with adjustable frequency, gain, Q, enable state and eight filter shapes. |
| 11 | Independent L/R EQ | Assign each band to left, right or both. The response chart draws both sides. |
| 12 | AutoEQ | Import AutoEQ or compatible Equalizer APO parametric `.txt` presets. |
| 13 | Per-output profiles | Save route, volume, EQ and other DSP settings by Windows endpoint. Profiles load on output selection and supported default-device changes. |
| 14 | FIR convolution | Load mono or stereo PCM WAV impulses. Partitioned FFT convolution supports up to 131,072 resampled taps. |
| 15 | Crossfeed | Adjustable low-pass crossfeed with an interaural delay. |
| 16 | High-precision resampling | Double-precision PCM resampling with selectable linear or minimum phase and standard or steep filters. |
| 17 | Dither | TPDF and first-order noise shaping in the exclusive DSP quantizer, with a bypass option. |
| 18 | ReplayGain and R128 | Read gain tags, scan tracks and editions, and apply track or album gain with configurable headroom. ReplayGain scanning uses mono/stereo sources. |
| 19 | Clipping and peaks | Per-channel sample overs, 4× oversampled true-peak estimation, a clipping indicator and stereo limiter gain-reduction readout. The limiter accounts for reconstruction across block boundaries and reserves 0.6 dB for differences between reconstruction filters. |
| 20 | Polarity and balance | 180° polarity inversion and independent balance trim. |
| 21 | Folder browsing | Browse physical folders and parent directories alongside track and album views. |
| 22 | Deep metadata | Show and search all FFmpeg-readable tags, including custom and classical fields. |
| 23 | Edition grouping | Albums group by album artist and title. Editions distinguish folder, codec, sample rate and available release tags. |
| 24 | DR meter | Track scan reports a dynamic-range estimate alongside loudness and true peak. |
| 25 | Spectral check | Samples several regions of high-resolution PCM for steep frequency cutoffs and flags possible bandwidth limits or upsampling. The result is a heuristic. |
| 26 | Integrity | Compare decoded FLAC with the embedded STREAMINFO PCM MD5. Other files show a SHA-256 hash. |
| 27 | Smart playlists | Save named filter combinations for codec, sample rate, bit depth, DR, genre, year, rating and play count. |
| 28 | Artwork and liner notes | Embedded and folder artwork plus a local PDF booklet viewer. |
| 29 | Signal path | Shows source format, active DSP and gain, stream precision, device, and Windows mix format for shared output. |
| 30 | Live meters | Source and output peak/RMS, sample overs, selected-channel waveform/FFT, output dBTP, momentary LUFS, and a front L/R phase scope with correlation. Raw DSD routes show transport details. |

## Verification

Generated fixtures verified exclusive 16-bit/44.1 kHz and 24-bit/96 kHz output on Realtek, 5.1 routing to the 7.1 Hi-Fi Cable, distinct stereo HRTF output, and sample-exact gapless CUE joins. Codec decoding, DSF-to-DoP payload packing, minimum-phase output length, stereo FIR, AutoEQ import, ReplayGain/R128 and FLAC MD5 were also exercised.

The FFmpeg test corpus verified PCM decoding from sample APE, TAK, Musepack SV7 and SV8 files. Adapted synthetic SACD images verified stereo DSD and stereo/5.1 DST extraction, PCM decoding, and DoP marker packing. End-to-end native DSD and DoP lock still need a compatible DAC. Commercial SACD image verification still needs a real disc image.

The true-peak meter passed the published tolerances for EBU Tech 3341 sequences 15–23. With the limiter set to −1 dBTP, the loudest of those limited sequences measured −1.60 dBTP in Soundroom. Exclusive PCM, binaural and HeSuVi routes each completed silent playback test tracks with zero underruns during metadata probing.

An isolated desktop build rendered the first page of an indexed 100,000-track fixture in 1.69–2.19 seconds across three launches. The working set five seconds after the first page rendered was 189–192 MiB. Both measurements exceed the 500 ms startup and 150 MB idle targets. A separate 512-file scan against the same size catalog overlapped 96 kHz exclusive playback with zero underruns. A forced low-reserve test verified that scanning pauses and resumes.
