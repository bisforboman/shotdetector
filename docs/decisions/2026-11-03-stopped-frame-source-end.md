# How frames from the caller say they were stopped

**Question** (PR #730, reported by shotdetector-web on 3.28.0): `Detect(path)` ends a run stopped by its end time at the
last frame's time plus one frame at the average rate (scenedetect's stop), and a run read to the end of the file at the
frames read over the average rate (its end of the stream). Frames from the caller always took the second. With
`YuvVideo.FrameTimestamps` given, frames ending before the last entry are now a stop. Without them a stop and the
video's end look the same, and the two ends differ on a variable rate and, in the last digits, at a constant rate whose
frames aren't on its grid in the time base (VP9 WebM at 29.97 fps in 1/1000, Sintel's 24 fps trailer). How should a run
without the timestamps tell that it was stopped?

**Choices**

1. Hosts give timestamps: no new API; document that a stopped run needs `YuvVideo.FrameTimestamps` (a WebCodecs host
   has every chunk's timestamp from its demuxer).
2. An explicit stop flag: a new public way (an option, a token) to say the frames stopped, with or without timestamps.
   New API surface.
3. `YuvVideo.FrameCount`: frames ending before it are a stop. A count that is only an estimate makes a whole run end
   as a stopped one; WebM has no count.

**Answer**: 1, hosts give timestamps. `FrameInputTests.AStoppedRunEndsWhereTheFileStoppedThereDoes` covers the variable
rate, 25 fps, 29.97 fps in 1/1000 (VP9 WebM) and 24 fps in 1/1000 (H.264), in-process and through the executable.

Since scenedetect 0.7.3 (the default compatibility mode from #768), the end of the stream is the last frame's time plus
one frame as well (its #561), so in that mode a stop and the end give the same end; the distinction above holds for
`PySceneDetectVersion.V0_7_2`.
