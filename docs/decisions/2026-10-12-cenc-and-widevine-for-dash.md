# CENC and Widevine for DASH (2026-10-12, issue #171)

FFmpeg's mp4 muxer already encrypts DASH segments (cenc-aes-ctr through the dash muxer's `format_options`; checked to
decrypt back exactly) but writes no `pssh` box and no MPD `ContentProtection`. Asked how to offer Widevine: **an
`Encryption` option on RemuxOptions** (key ID, key, `Widevine`), over a PSSH builder only, or documenting
`format_options` only. The pssh box and ContentProtection are ours, checked against `pssh-box.py` in CI.
