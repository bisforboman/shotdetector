# Unknown InputOptions names throw, as a fix (2026-10-19)

### Question

FrameReader drops an `InputOptions` name nothing used, without a word; the README says unknown option names are an
`ArgumentException`. How should it go?

`Demuxer.Open` (behind `FrameDecoder`, `MediaProbe`, `AudioReader`, `Audio` and `Remux`) copies `InputOptions` into the
dictionary avformat_open_input takes, then freed what was left: a misspelt name (`framerat`, `rw_timout`) did nothing,
silently. `CodecOptions` has refused its leftovers since 2026-10-18, as the encoders' options do. ffmpeg's command line
refuses an input option nothing used too ("Option not found"). ShotDetector's callers were reviewed: they pass only
options their inputs take (`protocol_whitelist` always, `rw_timeout` for http(s), `tls_verify` for https, `framerate`
for an image sequence; a Stream gets `protocol_whitelist=pipe`).

### Choices

1. **Throw, as a fix**: FrameReader 2.8.0, under `### Fixed` (the README already promised it).
2. **Throw, as a breaking change**: FrameReader 3.0.0.
3. **Fix the README instead**, and keep ignoring unknown names.

### Answer

**1. Throw, as a fix.** After avformat_open_input, the first name left over is an `ArgumentException` naming it and the
format FFmpeg opened the input as (the input is closed first).

Which leftovers count: everything left is either the caller's or one of FrameReader's own defaults (`scan_all_pmts=1`,
taken by MPEG-TS only; `tls_verify=1` and `ca_file`, taken by TLS only), which are left over on most inputs. So those
names, and the TLS options' aliases `verify` and `cafile`, never count, also when the caller passes them: a caller can
send `tls_verify=0` whatever the input is (and FrameReader's own HLS test passes `tls_verify=1` for an http playlist
whose segments are https, where the playlist's http protocol leaves it over). Every other leftover is the caller's,
so the check needs no list of what the caller set.

Correction (2026-10-19, ShotDetector 3.10.1): not all of ShotDetector's options were taken. It added `framerate` (with
`FrameRate` set) to any file path containing a `%`, also a plain video file such as `My%20Video.mp4`, which mp4 doesn't
take: 3.10.0 failed there in-process ("No demuxer or protocol option "framerate""; the executable always had). It now
adds it only to a path FFmpeg's image2 reads as a numbered sequence (`VideoReader.IsImageSequencePattern`).
