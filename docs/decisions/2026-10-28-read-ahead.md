# Read-ahead in VideoFrameReader (2026-10-28)

### Question

Issue #566: overlap decoding with the caller's per-frame work. What should be built?

A worker that moved from `ffmpeg -vf fps=1,scale=... frame_%06d.jpg` to the README's loop (`TryReadForwardTo`, then
`ImageEncoder.Encode` and `File.WriteAllBytes`) found it 25-35% slower in wall time on a machine with spare cores:
decoding is as fast as ffmpeg's, but the ~5 ms per frame of encoding and writing sits between two reads, while ffmpeg 8's
threaded pipeline encodes one frame as the decoder works on the next. The frame lifetime rule (a `VideoFrame` is valid
until the next read) keeps a caller from moving the encoding to another thread.

### Choices

1. **Read-ahead option**: `VideoFrameReaderOptions.ReadAhead`, frames decoded on a background thread while the caller
   works, the lifetime rule kept; callers get the overlap without changing their loop.
2. **Read-ahead + owned copy**: the option, and a `VideoFrame` copy that `ImageEncoder` takes, so a caller can hand
   frames to threads of its own.
3. **Pipelined image helper**: a call that writes the images of a list of times, decoding and encoding on separate
   threads inside (as `ffmpeg -vf fps=... %06d.jpg`).
4. **Backlog row only**: write it down, build nothing now.

### Answer

**1. Read-ahead option** (the owner, 2026-10-28).

As built: `ReadAhead = N` (default 0, today's behaviour) starts a thread at the first read that decodes (with
deinterlacing and rotation) and takes each frame (`FrameDecoder.TakeFrame`, a reference, no copy) into a queue of at
most N. The read takes frames from the queue where it called the decoder before; seeks and `TryReadForwardTo`'s jumps
pause the thread, drop what it read ahead and seek the decoder themselves. The conversion stays in the read: the frame
`TryReadForwardTo` gives isn't known before it is asked for, and converting every frame ahead would waste the work on the
frames it passes over (24 of 25 at a picture a second). So no ring of output buffers is needed, and the lifetime rule
holds as it is. A target within a second of the frame read last needs no look at the container's index (its keyframe is
at or before the target), so a picture every second never pauses the thread; reading the index otherwise does, as the
demuxer on the thread may add to it.

Measured on the issue's clip (5-minute 1920x1080 25 fps h264 testsrc2, x264 veryfast, keyframe every 2 s; 300 JPEGs at
qscale 2) in a 4-vCPU cloud container: there every core decodes, so ReadAhead changes little (the CLI and the loop alike
take 13-14 s); with the decoder held to 2 threads, standing for spare cores, ReadAhead 4 took the loop from 23-25 s to
20.6-20.8 s, the time of decoding alone (20.5-21.6 s). The pull request has the table. Identical JPEGs at every value.
