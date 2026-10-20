# Backlog

What ShotDetector does next, in priority order. Three sections: **Next** (open work, the first row first),
**Waiting** (rows that wait on the owner or an outside event) and **Maybe** (ideas not committed to). Update a
status when work starts or lands; add an item when a divergence or a gap turns up, with why it matters, in Next or
Maybe by its status. A row moves to [backlog-done.md](backlog-done.md) when it is Done, Dropped or Won't do.
Design questions go to [decisions/](decisions/).

Status: **Open**, **In progress**, **Maybe**; finished rows (in backlog-done.md): **Released** (on nuget.org),
**Done** (on `main`, in the next release), **Dropped**, **Won't do**.

## Next

- Fix every divergence the real-world check finds
  - Why: The promise is identical results
  - Status: Open (2026-10-19: on x86 one known, stats only: a video whose colour tags change midway, with FastYuv or, on a 10-bit siting change, the executable's default path (every cut exact; the per-frame-tags row in Maybe, the informational samples sintel_matrix_change.mkv and sintel_10bit_siting_change.mkv); on ARM the default results differ by FFmpeg's conversion rounding, documented in docs/decisions/2026-10-17-arm-results.md, while ShotDetector.FastYuv gives x86's results on 8-bit 4:2:0 and 4:2:2 and 10-bit 4:2:0 of even width, and MPEG-1/2, MJPEG and MPEG-4 Part 2 decode as on x86)

- CI: the AOT binaries decode a clip with their bundled FFmpeg on Alpine, Windows and macOS too
  - Why: ci.yml's `aot` job runs the published binary on a clip (file, https URL, standard input) with our FFmpeg next to it on linux-x64 and linux-arm64 only; on linux-musl-x64 it runs without the musl libraries, and on win-x64 and osx-arm64 it only checks that the binary starts. The README says every release binary needs nothing installed (#446; checked by hand on linux-x64 only, and 3.11.0's musl archive does carry the libraries), so the other three should prove it the same way
  - Status: Open

- Release: run each binary from its archive before uploading it
  - Why: release.yml's `binaries` job publishes, archives, attests and uploads without running the binary; unpacking the archive into an empty folder and running `--version` and one small clip (with no ffmpeg on PATH) would catch a broken archive, a missing library or a wrong rpath before users download it. The linux-arm64 entry first ran at 3.11.0
  - Status: Open

## Waiting

- macOS: sign and notarize the osx-arm64 binary, then perhaps a Homebrew tap
  - Why: release.yml doesn't codesign or notarize it, so a copy downloaded in a browser is quarantined and Gatekeeper blocks it until the user clears it (`xattr -d com.apple.quarantine`); notarizing needs the owner's Apple Developer account (secrets in the `release` environment). A Homebrew tap (formula pointing at the release asset) would make `brew install` the macOS route
  - Status: Waiting (notarizing waits on the owner's Apple Developer account; meanwhile README's "The release binaries" explains the quarantine and `xattr -dr com.apple.quarantine`, #446)

- At the next FrameReader release, count the commits touching both products again (docs/frame-reader-product.md, "When to reconsider")
  - Why: 2.8.0 counted 1 of 21, under 1 in 20 for the first time; the trigger needs two releases in a row over at least 20 counted commits, and then means asking the owner, not splitting
  - Status: Waiting (on the next FrameReader release)

- Move to FFmpeg 9's libraries (FFmpeg.AutoGen 9, Native packages on 9.0.x) when opencv-python's Linux wheels bundle FFmpeg 9
  - Why: scenedetect's frames come from OpenCV's FFmpeg; moving earlier risks bytes OpenCV doesn't give, and drops system FFmpeg 8 in-process
  - Status: Maybe (2026-10-14: FFmpeg.AutoGen 9.0.1.1 is out; opencv-python 5.0.0.93 and 4.14.0.94 still bundle libavcodec 62, so the trigger hasn't fired; the user: wait for OpenCV)

- The owner files the x264 AVX-512 report with VideoLAN (x264-avx512-report.md in the main checkout)
  - Why: CI's VideoWriter step needs DOTNET_TieredCompilation=0 because x264's AVX-512 code and .NET's tiered JIT differ about 1 run in 250 (CLAUDE.md); the report was written but not filed as of the 2026-10-15 reminder (it waited on a VideoLAN GitLab account). A public post from the owner's account: theirs to file, never a session's
  - Status: Open

## Maybe

- FrameReader: rtsp listen mode needs `listen_timeout` with FFmpeg 8.1
  - Why: Found by #443's test: with `rtsp_flags=listen` and no `listen_timeout`, FFmpeg 8.1's default of -1 s reaches tcp as -1000 ms, out of range ("Numerical result out of range"), on the plain command line too. Say so in FrameReader's README (InputOptions), or pass a default when `rtsp_flags` asks to listen; check whether a later FFmpeg 8.x fixes it first
  - Status: Maybe

- FastYuv: 10-bit 4:2:0 of odd width
  - Why: FastYuv returns null there (swscale's scaled path forces full chroma interpolation, done in C; docs/fastyuv-10bit-422.md, "Odd sizes"), so on ARM such videos keep ARM's conversion rounding instead of x86's results; the one ARM gap left in the formats FastYuv covers. Rare (odd widths are unusual in 10-bit sources)
  - Status: Maybe

- FastYuv and the executable path: convert with each frame's chroma location and colour tags, not the first frame's
  - Why: The default path converts each frame from its own chroma siting and colour tags (as OpenCV's sws_scale_frame does); since 2026-10-19 the executable path's scale filter and FastYuv take the first frame's (`FrameDecoder.ChromaLocation`/`ColorSpace`/`ColorRange`, ffprobe's frame entries; docs/decisions/2026-10-19-chroma-and-colour-tags-from-the-frame.md) for the whole video, so a video whose tags change midway still differs there. Measured on built clips (samples/informational/sintel_matrix_change.mkv, sintel_10bit_siting_change.mkv): FastYuv on a matrix or range change, and the executable's default path on a 10-bit siting change, differ in stats after the change only, with every cut exact and 0 CSV cells on every path since #432; not seen in a real file (docs/decisions/2026-10-19-per-frame-tags.md)
  - Options: B1, frames whose tags differ take the in-process BGR fallback (about 5 lines, no speed cost on x64, but ARM's rounding on those frames); B2, a FastYuv converter per tag set chosen per frame (30-60 lines, x86's results on ARM too); C, the executable path's FastYuv from showinfo's per-frame colour line (needs pairing two stderr lines, then B2's choice); D, the executable's default path on a siting change: ffmpeg's scale never follows a frame's siting, so only by restarting ffmpeg at the change (files only, a restart and a GOP per change)
  - Status: Maybe (the owner, 2026-10-19: parked)

- The first frame's colour tags read only when they'll be used
  - Why: The review of 3.10.0 (2026-10-19): ShotDetector reads the first frame's chroma location and colour tags once per detection (`FrameTags`: in-process FrameDecoder, else a one-frame ffprobe) also where only a fallback would use them (in-process without FastYuv, where BgrConverter converts each frame from its own tags), about 0.14 s on 4K 10-bit, and on a live rtsp URL it waits for the first frame before detection starts. Reading them lazily, when the executable path or FastYuv first needs them, would save both
  - Status: Maybe

Finished rows (Done, Released, Dropped, Won't do): [backlog-done.md](backlog-done.md). What the owner decided and
why: [decisions/](decisions/).
