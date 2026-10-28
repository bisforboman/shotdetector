# Skipping unreferenced frames before the wanted one (2026-10-28)

### Question

Skipping unreferenced frames before the wanted one: what should be built?

After ReadAhead (#566, #568) a picture a second with `TryReadForwardTo` + `ImageEncoder` was level with
`ffmpeg -i in.mp4 -vf fps=1:start_time=0:round=up,scale='min(1280,iw)':-2 frame_%06d.jpg`: both decode-bound. A
packet before the wanted frame whose frame nothing references can stay out of the decoder without changing any other
frame's bytes (a non-reference frame doesn't change the decoder's reference state). The CLI decodes them all.

Measured in phase 1 (4-vCPU cloud runner, BtbN FFmpeg 8.1.3, a prototype that dropped such packets before
`avcodec_send_packet`, never through `skip_frame`). Every source was byte-identical with and without, on frames and
tags (50 sources, 4 configurations each).

Share of droppable packets:

| Source | Codec | Droppable |
|---|---|---|
| issue #566's clip (testsrc2, x264 veryfast, keyint 50) | h264 | 29.8% |
| Sintel trailers (480p/1080p, retimed, rotated, 10-bit, 4:2:2, 4:4:4) | h264 | 35-45% (b-pyramid: 259 of 714 B-frames are references) |
| the same with `b-pyramid=none` | h264 | 57.5% |
| Tears of Steel 720p | h264 | 52.1% |
| Sintel / Tears of Steel, x265 | hevc | 59% |
| `-bf 2` mpeg1/mpeg2/mpeg4 | mpeg1/2/4 | 66.6% |
| libvpx VP9 | vp9 | 0% (every inter frame refreshes LAST) |
| SVT-AV1 | av1 | 25% of temporal units (half its unreferenced frames share a packet with a reference frame) |

The demuxers' `AV_PKT_FLAG_DISPOSABLE` is set for HEVC in mp4 only, so the packet headers are parsed.

The issue's loop (MaxWidth 1280, JPEG q2, files written), mean of interleaved runs, prototype:

| Source | Decoder threads | ffmpeg CLI | ReadAhead 0, all → skipping | ReadAhead 4, all → skipping |
|---|---|---|---|---|
| issue clip, 300 frames | default | 19.0 s | 20.7 → 15.9 s | 19.8 → 16.4 s |
| issue clip | 2 | 30.0 s | 33.1 → 23.9 s | 29.9 → 23.2 s |
| Tears of Steel h264, 3 min | default | 6.9 s | 7.2 → 4.7 s | 7.0 → 4.9 s |
| Tears of Steel hevc, 3 min | default | 9.0 s | 9.4 → 7.6 s | 9.5 → 7.4 s |

Found on the way: packed DivX B-frames broke (the decoder brings a packed B-frame out with the next packet), so they
are a guard; two bugs on main were fixed first (a last frame without a timestamp; 4:2:2 turned by 90 degrees).

### Choices

1. **h264+HEVC+MPEG-1/2/4** (recommended): about 250 lines with the guards; HEVC 18-30% faster, MPEG-2's long GOPs two
   thirds B-pictures.
2. **h264 only**: most of the gain on typical sources, fewer guards.
3. **Don't build**: stay level with the CLI.

### Answer

**h264+HEVC+MPEG-1/2/4** (the owner, 2026-10-28), with every guard from the measurement: DivX packed B-frames,
progressive field order (H.264: its SPS's frame_mbs_only_flag), faulty pts, the reorder depth, no VP9/AV1, off when
deinterlacing or on the GPU; in TryReadForwardTo, ReadAt and the TryTake methods.

Built as `UnreferencedPackets` (FrameReader), which `FrameDecoder.SkipBefore` consults for keyframes and for packets
before the target:

- H.264: non-IDR slices with nal_ref_idc 0, once an SPS was seen that is frame-coded (frame_mbs_only_flag 1) and has
  a VUI bitstream_restriction. Without one FFmpeg guesses the reorder depth from the frames it sees. Such an SPS, or a
  field-coded one, turns it off for good. After a seek only from the next IDR: an MPEG-TS seek can land before one,
  and FFmpeg then holds back unrecovered frames by their output order, which a left-out frame changed on the Sintel
  trailer as TS.
- HEVC: sub-layer non-reference pictures (even NAL types up to 14) of the highest TemporalId the SPS declares; field
  order progressive or unknown.
- MPEG-1/2: B-pictures only, no sequence or GOP header; MPEG-2 only when progressive.
- MPEG-4 Part 2: one B-VOP, no headers; user data `DivX...p` turns it off.
- Any codec: a packet without a pts turns it off (a frame without a timestamp gets the one before's plus its
  duration, which a left-out frame would change), and so does a frame whose best-effort timestamp isn't its pts.
- Where: `TryReadForwardTo`/`TryTakeForwardTo` skip before their target. `Seek` (`TryReadAt`, `TryTakeAt`) skips
  before ffmpeg's trim point. `ReadAt` sets each reader's next time as soon as a frame is handed over, so decoding
  ahead leaves those out while the handler works. That is the look-ahead recovery, exact because the next read is
  forward to that time.

Measured as built (same runner; mean of 2 interleaved runs; issue #566's clip, 300 pictures):

| | ffmpeg CLI | every packet | skipping |
|---|---|---|---|
| TryReadForwardTo loop, default threads, ReadAhead 0 | 17.8 s | 19.1 s | 15.0 s |
| TryReadForwardTo loop, 2 decoder threads, ReadAhead 4 | 28.7 s | 28.1 s | 21.6 s |
| ReadAt, 2 readers (encoder per thread) | | 16.9 s | 13.2 s |

Tests: SkipUnreferencedTests: 15 generated sources (x264 with and without b-pyramid, open GOP in TS, MBAFF, x265 in mp4
and TS, mpeg1/2/4 with B-frames in mkv/ts/mpg/mp4/avi, interlaced MPEG-2, packed Xvid, VP9). For each: frames and tags
identical with and without, forward at 1 s and 0.36 s, seeks, takes, ReadAhead 0/4, threads 1/default, and ReadAt
with a slow handler; packets left out where expected, none where a guard holds. The packet rules are tested on SPSs as
x264 and an Apple encoder write them. Mutation lines for each guard.
