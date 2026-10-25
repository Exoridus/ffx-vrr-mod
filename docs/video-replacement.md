# Replacement movies

The mod ships an empty `video/` folder. Put optional AV1 replacements for the game's movies there.
Nothing is bundled; a missing or unsupported file keeps the original movie.

## File names

| File | Replaces |
| --- | --- |
| `opl_us.webm` | the US movie |
| `opl_jp.webm` | the JP movie |
| `opl.webm` | both, when no localized file exists |

`.mkv` works with the same names. Localized names win over shared ones; `.webm` wins over `.mkv`.

## Format

- One AV1 video track, 8- or 10-bit 4:2:0, even dimensions up to 3840x2160.
- 29.97 or 59.94 fps declared in the track header.
- Original Vorbis audio. A shared file can carry both tracks with ISO language `eng` and `jpn`;
  a JP request prefers `jpn`, every other request uses `eng`. More than one match is rejected.
  Track titles are informational. Do not use FLAC audio.

Decoding uses D3D11 hardware acceleration with dav1d as the software fallback. 4K software decoding
can exceed the CPU budget. Output goes through the game's 8-bit SDR renderer.
