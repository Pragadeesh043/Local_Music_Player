# Soundroom

Soundroom is an offline Windows 11 x64 music player with headphone surround, configurable DSP, and a live multichannel audio inspector.

Open `artifacts\Release\Soundroom.exe` with the other files in its directory. The app looks for FFmpeg and ffprobe under `Tools\FFmpeg` first. The library and profiles live in `%LOCALAPPDATA%\Soundroom`.

```powershell
.\build.ps1
```

Open `Soundroom.slnx` in your IDE. The build uses a workspace-local SDK when available and falls back to a system .NET 10 SDK. Use `.\build.ps1 -Configuration Debug` for a debug build, or add `-NoRestore` when packages are already restored for this project.

For a project-local setup, place standalone `ffmpeg.exe` and `ffprobe.exe` in `.tools\ffmpeg`. The build copies them alongside the app. The workspace-local SDK is in `.dotnet`, NuGet packages are in `.nuget\packages`, and optional Python verification tools use `.venv`. Runtime library data and logs are stored per user.

## Removable music folders

Plug in the drive and select **Watch folder** once. Choose the folder holding your collection or the drive root. Soundroom indexes metadata as it reads the existing files and plays music from the source. Later scans use file size and modification time to identify changes. Select **Add files** to import a disc image explicitly.

The sidebar shows which registered folders are connected. A disconnected source keeps its indexed tracks and ratings and displays an offline message if selected for playback. Reconnect the drive and press Play to resume an interrupted track. Windows volume identity keeps a registered folder recognizable when the drive letter changes.

## Workspace

| Location | Contents |
| --- | --- |
| `src/Soundroom/UI` | WinUI views, transport, library browsing, and output settings |
| `src/Soundroom/Audio` | Playback and output drivers, decoding, DSP, and analysis |
| `src/Soundroom/Library` | SQLite, metadata, CUE and SACD imports, and EQ presets |
| `src/Soundroom/Infrastructure` | Application diagnostics |
| `src/Soundroom/Native` | Runtime dependencies and upstream notices |
| `artifacts` | Runnable builds, grouped by configuration |

Playback and import errors appear in the status bar. Technical details are recorded in `%LOCALAPPDATA%\Soundroom\Logs\Soundroom.log`, with one rotated log retained. The log may contain local track names and paths.

## Audio buffering

A playback worker handles decoder reads, DSP, and meter calculation. It feeds a preallocated single-producer, single-consumer ring buffer. The output provider consumes queued blocks and advances the track position as it delivers frames. Each session buffers up to approximately half a second of audio, with a 16 MiB cap on queued audio data.

If the ring runs dry, processed playback reports the underrun and supplies silence until audio is available. Direct PCM and DSD stop and report the underrun to preserve the integrity of the stream.

## Output modes

- **Binaural headphones** renders supported multichannel layouts through Steam Audio’s default HRTF. Select the stereo output connected to your IEMs.
- **Stereo downmix** folds the source to two channels and applies the headphone DSP chain.
- **7.1 device / HeSuVi** sends mapped channels to an eight-channel Windows endpoint. It leaves spatial processing and volume to the external chain.
- **Exclusive · direct PCM** negotiates a WASAPI format at the track’s rate and preserves the decoded integer samples. It rejects devices that lack a compatible format. The inspector shows the valid bits and any wider transport container.
- **Exclusive · DSP / resampling** applies processing in double precision, uses the requested PCM rate and precision, then quantizes to the negotiated output format.
- **Exclusive · DSD over PCM** sends native DSF or uncompressed DSDIFF payloads in a DoP carrier. Enable the DoP capability for the selected DAC in **Output settings**.
- **ASIO · native DSD** opens the selected ASIO driver in one-bit DSD mode. It requires a 64-bit driver with native packed DSD buffers, the source’s clock rate, and sufficient output channels.

The signal path describes the samples handed to Windows or ASIO. Internal DAC DSP and physical clock readback depend on the hardware. Use **Output settings** to save the route, DSP, resampler, RAM option, and volume mode for the output device.

## Feature coverage

| # | Feature | Implementation |
| --- | --- | --- |
| 1 | Bit-perfect playback | Strict WASAPI Exclusive integer bypass. Fixed digital gain, DSP and sample-rate conversion bypassed. |
| 2 | Automatic sample-rate and bit-depth switching | Per-track negotiation, with transparent padding to a supported wider word where required. |
| 3 | Lossless PCM | FLAC, ALAC, PCM WAV, AIFF/AIFC and WavPack through FFmpeg. |
| 4 | DSD and SACD | DSF and DSDIFF with PCM fallback, DoP and native ASIO routes for uncompressed DSD. The supported rate depends on the selected device and driver. SACD ISO import uses the packaged extractor for stereo and multichannel areas and caches extracted DSF tracks locally. DST-compressed standalone DFF uses the PCM route. |
| 5 | Lossy and legacy | Opus, Ogg Vorbis, AAC, MP3 and Musepack SV7/SV8. CUE sheets appear as individually playable tracks linked to the original source file. |
| 6 | 64-bit floating-point pipeline | Double-precision decoding, mix, volume, EQ, FIR and PCM quantization. Steam Audio’s native HRTF boundary and shared WASAPI use 32-bit floating point. |
| 7 | Gapless playback | A prefetched decoder joins compatible neighboring tracks in one WASAPI stream. A device format or speaker-layout change can reopen the stream. |
| 8 | RAM playback | Decode the current and next tracks into RAM, bounded to 256 MiB per track. |
| 9 | Digital and hardware volume | Digital attenuation, fixed output, and endpoint hardware volume when the Windows driver advertises it. |
| 10 | Parametric EQ | Up to 64 bands with adjustable frequency, gain, Q, enable state and eight filter shapes. |
| 11 | Independent L/R EQ | Assign each band to left, right or both. The response chart draws both sides. |
| 12 | AutoEQ | Import AutoEQ or compatible Equalizer APO parametric `.txt` presets. |
| 13 | Per-output profiles | Save route, volume, EQ and other DSP settings by Windows endpoint. Profiles load on output selection and supported default-device changes. |
| 14 | FIR convolution | Load mono or stereo PCM WAV impulses. Partitioned FFT convolution supports up to 131,072 resampled taps. |
| 15 | Crossfeed | Adjustable low-pass crossfeed with an interaural delay. |
| 16 | High-precision resampling | Double-precision PCM resampling with selectable linear or minimum phase and standard or steep filters. |
| 17 | Dither | TPDF and first-order noise shaping in the exclusive DSP quantizer, with a bypass option. |
| 18 | ReplayGain and R128 | Read gain tags, scan tracks and editions, and apply track or album gain with configurable headroom. ReplayGain scanning uses mono/stereo sources. |
| 19 | Clipping and peaks | Per-channel sample overs, oversampled true-peak estimate and optional stereo peak limiter. |
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
| 30 | Live meters | Source and output peak/RMS, sample overs, selected-channel waveform/FFT, output dBTP, momentary LUFS and front L/R correlation. Raw DSD routes show transport details. |

## Verification

Generated fixtures verified exclusive 16-bit/44.1 kHz and 24-bit/96 kHz output on Realtek, 5.1 routing to the 7.1 Hi-Fi Cable, distinct stereo HRTF output, and sample-exact gapless CUE joins. Codec decoding, DSF-to-DoP payload packing, minimum-phase output length, stereo FIR, AutoEQ import, ReplayGain/R128 and FLAC MD5 were also exercised. End-to-end native DSD, DoP DAC lock, and real SACD ISO imports still need compatible hardware and an image fixture.
