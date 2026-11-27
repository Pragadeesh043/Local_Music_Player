# Changelog

## v1.0.3

### What's changed

- **Playback** — PCM fades out before pausing and fades back in on resume. Pressing Play during the fade reverses it. Adjust the fade from 250 to 600 ms in **Output settings → PCM processing**. This applies to shared and exclusive PCM, including PCM with DSP enabled.
- **Discover** — Each launch samples up to five featured albums from the full library and avoids repeating the same set when more albums are available. The slideshow advances about every 6.5 seconds, pauses on hover or keyboard focus, and resets its timer after manual navigation.
- **Metadata** — Corrected UTF-8 handling for FLAC and ID3 tags with accented characters. Existing garbled entries need to be re-read from the original files.
- **Lyrics** — Added soft fades at the edges of the lyrics panel, smoother scrolling, and clearer emphasis on the current line.
- **Live Signal** — Added translucent plots, album-colored spectrum and level meters, and smooth peak-hold markers.
- **Album view and transport** — Replaced repeated artwork and album names with track numbers, added an animated playing indicator and an artwork glow, and aligned the track title and format badge in the playback bar.
