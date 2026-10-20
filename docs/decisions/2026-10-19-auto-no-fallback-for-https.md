# Auto doesn't hand an http(s) URL the libraries failed to open to the executable (2026-10-19)

### Question

When the in-process open of an http(s) URL fails, what should Auto do?

Found reviewing FrameReader 2.8.0 and ShotDetector 3.10.0: under `Decoder = Auto`, a URL whose protocol the libraries
have is probed in-process first, and when that throws, the ffmpeg executable takes the URL over without a word. The
executable checks an https URL's own certificate, but fetches the https segments, keys and variants an HLS playlist
names without the check (FFmpeg's HLS demuxer doesn't pass `tls_verify` on). Tested: an http playlist naming segments
on a self-signed https server is refused in-process (FrameReader checks every URL the input opens), and Auto then
decoded it through the executable, unchecked; only the HLS warning said so.

### Choices

1. **Refuse and name the cause**: for an http or https URL, Auto reports the in-process failure instead of falling
   back. Files and other URLs keep falling back as now; `Decoder = FfmpegProcess` still uses the executable (and warns).
2. **Fall back but warn first**: keep the fallback and say why it happened in `Warnings`.
3. **An opt-in fallback option**: refuse by default, with an option to allow the executable.

### Answer

**1. Refuse and name the cause** (owner, 2026-10-19). `VideoReader` rethrows the in-process probe's
`ShotDetectionException` for an http or https URL (same `Reason`), its message ending in why Auto doesn't take it to the
executable and that `Decoder = FfmpegProcess` (`--decoder process`) does. Shipped in ShotDetector 3.10.1 under
`### Fixed`.

Consequences:
- The in-process failure text doesn't tell a refused certificate from a format the libraries lack (both are FFmpeg's
  "Invalid data found when processing input"), so both refuse. DASH over http(s) with FrameReader.Native (which leaves
  out libxml2, so has no DASH demuxer) worked under Auto only through this fallback; it now needs
  `--decoder process`. With libraries that have the DASH demuxer (a system FFmpeg, BtbN's builds), Auto decodes it
  in-process as before.
  Asked again with this consequence in front of the owner (2026-10-20, multiple choice: "Keep it, merge" or "Exempt
  DASH", a fallback only on an error that tells a missing DASH reader from a refused connection): "Keep it, merge".
- Only URLs whose protocol the libraries have are tried in-process at all: on libraries without https (or tls), an
  https URL still goes straight to the executable, as before.
