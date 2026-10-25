Optional AV1 videos

Place replacement WebM or MKV files here. No test manifest or sidecar is required.
The VideoOverride setting enables or disables optional movie replacements.
Missing or unsupported files keep the original game movie.

Names:
  opl_us.webm: replacement for the original US movie
  opl_jp.webm: replacement for the original JP movie
  opl.webm: shared video, selected when the localized replacement is absent

The same names with .mkv are also supported. Localized names take precedence
over shared names; .webm takes precedence over .mkv for the same stem.

Shared files can carry both original Vorbis audio tracks. Set their ISO language
fields to eng and jpn. A JP movie request prefers jpn and falls back to eng;
all other requests select eng. Track titles such as opl_us and opl_jp are
informational and do not control selection. Multiple matches are rejected.
Do not use FLAC audio from the viewing masters here.

Supported: one AV1 video track, 8/10-bit 4:2:0, even dimensions up to3840x2160,
29.97 or59.94fps declared in the track header. D3D11 hardware decoding is
preferred; dav1d is the fallback. Output uses the game's8-bit SDR renderer.
An active FMV lab manifest takes precedence over this directory.
