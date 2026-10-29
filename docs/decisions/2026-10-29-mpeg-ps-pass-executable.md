# MPEG-PS through the executable: the timestamp pass (2026-10-29)

Through the ffmpeg executable, an MPEG-PS file (packets without a pts) took ffprobe's decoding pass for every frame's
timestamp (`frame=pts,pkt_dts`: its pts when set and non-zero, else its pkt_dts, else 0, as OpenCV labels its frames)
before detection decoded the file again. In-process the timestamps come with the frames since #546
(docs/decisions/2026-10-27-pre-scan.md). The backlog row asked first whether the executable's own per-frame times,
showinfo's with `-copyts` as files run, reproduce the pass's.

## What showinfo gives

FFmpeg 8.1.3 (CI's BtbN pin), compared frame by frame on the fixture clips (clean, cut short, holed, self-joined),
sintel_mpeg2.mpg and sintel_mpeg1.mpg with cut, holed, self-joined, three-way-joined and byte-damaged copies of each, a
`-f dvd` VOB, h264 in a program stream and a 4-minute 576p MPEG-2 PS:

- MPEG-1/2: every frame matches but the last of every file that has one without pts and dts (flushed out of the decoder
  at the end): the pass gives it 0 (so its position falls back to frame / fps, as scenedetect's does), showinfo a time
  the command line extrapolates. fftools/ffmpeg_dec.c sets `frame->pts = frame->best_effort_timestamp` and, where that
  is unset, `last_frame_pts + last_frame_duration_est`, before any filter sees the frame.
- h264 in PS: 345 of 1253 frames differ, every frame with neither pts nor dts. With `-debug_ts` (each decoded frame's
  pkt_dts) those can mostly be told apart (no pkt_dts, and a time that is no packet's pts: extrapolated), but 3 frames
  still came out wrong: libavcodec's `guess_correct_pts` picked the dts over a real, different pts (pts going backwards
  more often than dts), and the command line leaves no trace of the frame's own pts. Never seen on MPEG-1/2, but nothing
  there rules it out.
- Where the last frame has a real pts (the DVD VOB, the 4-minute 576p, the three-way joins), its time and an
  extrapolated one can't be told apart either: the pass would be needed anyway, so DVD-like files would gain nothing.

The pass alone took 3.3 s of a 10.8-11.8 s detection of the 4-minute 576p file (4 CPUs; PS is always read
sequentially).

## Question

The executable's per-frame times don't reproduce the pass's. What should the executable do for MPEG-PS?

## Choices

1. **A: the pass alongside detection.** ffprobe's pass starts with the read instead of before it; each frame takes its
   label from the pass's timestamps as they arrive, with the same PacketOf-over-arrived rule as in-process, waiting where
   the pass is behind. Exact by construction (the same values), for every codec in PS. Saves wall time where cores are
   free, not CPU. A start time, deinterlacing, seeks and streams keep the pass first.
2. **B: showinfo + `-debug_ts` for MPEG-1/2 only**, the pass where ambiguous. Not provably exact (the `guess_correct_pts`
   case), parses debug lines from several of ffmpeg's threads, and DVD-like files gain nothing.
3. **C: won't do.** Keep the pass first; record these findings.

## Answer

**A: the pass alongside** (the owner, 2026-10-29), with tests that the arriving path gives the same labels and CSVs as
the pass first on every PS file (h264 in PS, a DVD VOB, joins, damage), in 0.7.2 and 0.7.1; cancelling stops both
processes; an ffprobe failure mid-run fails as the pass first did.

`VideoReader.TimestampPass` runs ffprobe and hands its values over as it prints them (the pass first drains the same
class). A read from frame 0 through the executable starts it (`_passAlongside`) and labels each frame with
`PacketOfFromPass`: `PacketOf` over the values so far (`complete: false`), taking more while the answer isn't settled,
over all of them once the pass has ended. A read that reaches the end drains the rest and makes them the list; one that
stops early (a count, the caller, a failure) stops ffprobe, so a later frame's timestamp takes a pass, as in-process;
anything that needs every timestamp mid-read (reading again from a chroma siting change) waits for the rest of the pass.
`OnFrameTimestamps` (tests) still fires only for a pass taken first.

Measured on the 4-minute 576p PS (this container, 4 CPUs, five interleaved runs each, default detector): 7.8-8.4 s
wall instead of 10.6-11.5 s (about 27% less); CPU about the same, 24.9-26.5 s (user + system) instead of 26.3-28.5 s.

Checked byte-identical with main's executable path (scene and stats CSVs; every detector, frame skip, `-e`, min-scene-len
1; 0.7.2 and 0.7.1; one decoding thread) on all the files above. The byte-damaged MPEG-1 copy's stats differ run to run
on main too: FFmpeg's own decode of it isn't deterministic (`-f framemd5` differs between two runs), its scene lists
don't.
