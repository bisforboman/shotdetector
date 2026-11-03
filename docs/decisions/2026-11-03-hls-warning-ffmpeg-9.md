# The executable's HLS warning with FFmpeg 9 (2026-11-03)

### Question

`VideoReader.ExecutableHlsWarning` ([2026-10-19](2026-10-19-hls-warning-http-playlists.md)) says the ffmpeg executable
fetches an http or https HLS playlist's https segments, keys and variants without checking their certificates, whatever
its version. [2026-10-16](2026-10-16-https-certificates-and-tls.md) noted that FFmpeg's `tls_verify` defaults to off
"until libavformat 63", and CI's executable is now FFmpeg 9.0 (libavformat 63). Does FFmpeg 9 check them? If it does,
the backlog row's rule applies: warn only for libavformat 62 and older, and record it.

### What was measured

BtbN's builds pinned in `.github/actions/btbn-ffmpeg` (n8.1.3, libavformat 62.12; n9.0.2, libavformat 63.1, checksum
verified). `ffmpeg -h protocol=tls` already differs: 9.0 lists `tls_verify` as "default true". The test used a local
HTTP server for the playlists and an HTTPS server with a self-signed certificate (CN and SAN 127.0.0.1) for what they
name, with ShotDetector's own input options for an http URL (`-protocol_whitelist http,https,tcp,tls,crypto,httpproxy
-rw_timeout 30000000`, no `-tls_verify`):

| http playlist, then on the self-signed https server | FFmpeg 8.1 | FFmpeg 9.0 | 9.0, the certificate trusted (`SSL_CERT_FILE`) |
|---|---|---|---|
| the segments | all 150 frames | "certificate verify failed", segment 0 refused | all 150 frames |
| an AES-128 key (`#EXT-X-KEY` URI) | all 150 frames | refused | all 150 frames |
| a variant (master playlist naming a media playlist) | all 150 frames | refused | all 150 frames |

An https playlist on the self-signed server with `-tls_verify 1` is refused by both versions, as before. The trusted
column shows that 9.0 refuses because of the check and not something else. End to end, `shotdetect --decoder process`
on the http playlist with 8.1 gives the shots and the warning; with 9.0 and the certificate trusted, the shots and no
warning; with 9.0 and the self-signed certificate, the read fails ("Error when loading first segment").

### Answer

FFmpeg 9 checks every https connection by default, the HLS demuxer's nested opens included, so the warning is true only
up to libavformat 62. `ExecutableHlsWarning` now takes the executable's libavformat major version (`ffmpeg
-version`'s libavformat line, asked once per executable and only for an http(s) playlist decoded by the executable) and
warns for 62 and older, or when the version can't be read. Its sentence says so: "decoded by an ffmpeg older than
FFmpeg 9, which fetches any https segments, keys and variants without checking their certificates; FFmpeg 9 and
in-process decoding (the FrameReader.Native packages) check them."

Left as it is: `Auto` still doesn't hand an http(s) URL the libraries failed to open on to the executable
(`VideoReader.AutoHttpRefusal`). Its reason, the executable's unchecked segments, no longer holds for FFmpeg 9, but it
would have to ask the executable's version before falling back. That is in the backlog's Maybe section.
