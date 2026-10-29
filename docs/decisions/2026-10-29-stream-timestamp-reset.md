# A Stream whose timestamps start over (2026-10-29)

Two h264 .ts encodes joined, the second's timestamps starting over (TimestampResetTests' JoinedEncodes), content
detector, FFmpeg 8.1. As a file, both decoders gave cuts 50, 75, 100. As a Stream (`Detect(Stream)`,
`DetectStreamAsync(Stream)`, the CLI's `-i -`), in-process gave 50, 75, 100 and the executable 50, 100, 125, 175.

What scenedetect gives (0.7.2, opencv-python-headless 5.0.0.93, Linux x64): 50, 75, 100 on the file, and the same with
VideoStreamCv2's capture opened on `pipe:0` (the file on stdin) or on a Python stream object (OpenCV's stream input).
`CAP_PROP_POS_MSEC` goes from 3960 to 0.0 at frame 101 in all three: OpenCV, libavformat alone, gives the timestamps as
they are. The executable's times came from the ffmpeg command line itself: `ts_discontinuity_detect()` in
fftools/ffmpeg_demux.c (n8.1.3), for formats with AVFMT_TS_DISCONT (MPEG-TS), without `copy_ts`, adds a dts that goes
back more than 0.1 s (`pkt_dts + AV_TIME_BASE/10 < ds->dts`) or jumps more than `dts_delta_threshold` (10 s) to an offset
every later packet takes, so the times continue. `-dts_delta_threshold` can't turn the going-back case off; `-copyts`
does (except for a pts wrap, which libavformat corrects for every caller). Without `-copyts` it also shifts a stream's
first packet that is over 10 s from another stream's last, which OpenCV doesn't either.

## Question

A Stream whose timestamps start over: how should the two paths agree?

## Choices

1. **B: -copyts on streams.** Streamed runs of the executable take `-copyts`, as files do, and subtract the input's start
   from ffmpeg's own Duration line under "Input #0" (printed to the µs, so exact; the same connection that is read,
   where ffprobe's start of a live URL is another's), as its shift without `-copyts` did. Both give 50, 75, 100,
   scenedetect's. Clean streams are unchanged; files are untouched. A Stream through the executable can no longer tell
   its shots as they come where timestamps start over, as in-process already couldn't.
2. **A: port ffmpeg's correction in-process.** Both give 50, 100, 125, 175, which no scenedetect run gives, and a Stream
   would differ from the same bytes as a file. It would end the OnShot retelling gap on streams.
3. **C: document only.** The two decoders keep differing.

## Answer

**B: -copyts on streams** (the owner, 2026-10-29). `VideoReader.InputStart` reads the start (0 where the line has none,
`start: N/A`), `MicrosIn` takes it to the filter's time base as av_rescale_q rounds, and every streamed frame's showinfo
pts has it subtracted. TimestampResetTests check a Stream and `Streaming = true` on the file give the file's cuts with
both decoders, and that both tell the same shots on a Stream; two mutation lines break the `-copyts` and the subtraction.
