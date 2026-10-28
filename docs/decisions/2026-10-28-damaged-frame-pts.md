# Damaged files: each frame labelled by its own pts (2026-10-28)

### Question

corrupt.ts: label each frame from its own pts, as OpenCV does. What about the ffmpeg-executable path?

The Maybe row "corrupt.ts: two decoded frames with the same pts" reproduced on real samples: tools/make-broken.py's
corruption with other seeds on the Sintel 480p trailer stream-copied to TS (seed 2) and on the Sintel DivX mkv (seeds
0-2), and on 10 more of 110 damaged variants (FFmpeg 9.0 and 8.1, opencv-python-headless 5.0.0.93, scenedetect 0.7.2;
the same at one decoder thread and the default). After damage a late frame's pts goes back (Sintel's: frame 391 at
pts 1843500, frame 392 at 1614750); libavcodec's `guess_correct_pts` then takes the dts as the best-effort timestamp
(frame 392's is 1843500 again; later ones run a frame behind). OpenCV's CAP_PROP_POS_MSEC is the frame's own pts;
ShotDetector labelled from the best effort (FrameDecoder.Pts in-process; showinfo through the executable, since
ffmpeg's `video_frame_process` sets `frame->pts = frame->best_effort_timestamp`), and PacketOf moved every later
label one packet on until the next gap: cuts a frame late, 8-786 frames per file labelled differently.

Labelling by the frame's pts gave OpenCV's label on every frame of all 12 (simulated); built in-process, every cut of
8 of them exact and the scene list CSVs and stats timecodes identical (what differs is the threaded concealment, which
varies between runs of either tool: 2026-10-22).

### Choices

1. **In-process fix, document exe** (recommended): label in-process frames by their pts (best effort where none);
   README says the executable can't.
2. **Both paths**: also an ffprobe frames pass on damaged files for the executable (a second decode there, and a
   trigger to find them).
3. **Not now.**

### Answer

**In-process fix, document exe** (the owner, 2026-10-28). `VideoReader.LabelPts`; the seek still drops frames by the
best effort, as the executable's `select` does. Test: InProcessTests.LabelsEachFrameByItsOwnPtsAsOpenCv (an
intra-only clip with one frame's pts set back, which reproduces it without damage); CI: tools/make-broken.py's corrupt_copy.ts (the trailer's own bytes as TS, seed 2) in the
informational damaged-files step, as damaged files' results vary between runs.
