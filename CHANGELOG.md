# Changelog

## v1.0.4

### Added

- **Settings** shows the desktop build version and links to the GitHub repository, releases, and issues. It also provides access to output settings, DSP, and the keyboard reference.

- **Keyboard controls** for play and pause, five- and 30-second seeks, volume, mute, and shuffle. Text fields and keyboard-operated controls keep their own shortcuts. The sidebar includes a keyboard reference, and Windows media controls now handle Stop.
- **12 graphic EQ presets** alongside V, M, W, and U, including Flat, Mid-Hump, Warm and Bright Tilt, Telephone, bass and treble curves, Vocal Booster, Warm / Full, and Scooped. Preview the response and use **Apply DSP** to commit the curve, its trim, and peak protection. Flat resets the graphic bands and their preset effects.
- **E-AC-3 playback** in M4A with the bundled FFmpeg 8.0.3 decoder.

### Fixed

- **Now Playing** shares its album-art backdrop with the top bar. Keyboard focus uses a softer accent, and the sidebar keeps long device names within its width.

- **Playback errors** identify the failed track and keep FFmpeg details in the diagnostic log. A decoder failure clears stale signal readouts and leaves the remaining queue available through **Next**.
- **Shared 7.1** applies 3 dB of headroom before sample-rate conversion to reduce overs. The tested 44.1 kHz WAV stayed below full scale with both resampling phases and both filter widths.
- **DSD readouts** show the source rate separately from the PCM output rate. The Native ASIO DSD format rejection names the correct mode.

### Packaging

- NSIS and MSIX builds check for the E-AC-3 decoder and the MOV/M4A demuxer. MP4A selects FFmpeg and FFprobe together from the first complete tool directory.

## v1.0.3

### What's changed

- **Playback** — PCM fades out before pausing and fades back in on resume. Pressing Play during the fade reverses it. Adjust the fade from 250 to 600 ms in **Output settings → PCM processing**. This applies to shared and exclusive PCM, including PCM with DSP enabled.
- **Discover** — Each launch samples up to five featured albums from the full library and avoids repeating the same set when more albums are available. The slideshow advances about every 6.5 seconds, pauses on hover or keyboard focus, and resets its timer after manual navigation.
- **Metadata** — Corrected UTF-8 handling for FLAC and ID3 tags with accented characters. Existing garbled entries need to be re-read from the original files.
- **Lyrics** — Added soft fades at the edges of the lyrics panel, smoother scrolling, and clearer emphasis on the current line.
- **Live Signal** — Added translucent plots, album-colored spectrum and level meters, and smooth peak-hold markers.
- **Album view and transport** — Replaced repeated artwork and album names with track numbers, added an animated playing indicator and an artwork glow, and aligned the track title and format badge in the playback bar.
