# DASH packaging scope (2026-10-12, issue #171)

#171 asked for DASH packaging (fMP4 segments and an MPD), then CENC encryption, then a Widevine PSSH helper. Asked:
**DASH now, CENC and PSSH later** (over DASH plus CENC without PSSH, all three, or none). DASH is FFmpeg's dash
muxer behind a general `RemuxOptions.MuxerOptions`, exact against ffmpeg; CENC/PSSH would be our own code beyond
what FFmpeg writes, so it's a Maybe on the backlog.

Since the same day ([2026-10-12-cenc-and-widevine-for-dash.md](2026-10-12-cenc-and-widevine-for-dash.md)) CENC and the
Widevine PSSH are built too.
