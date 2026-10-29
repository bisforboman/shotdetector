### Faster

- `VideoFrameReader.TryReadForwardTo` and `TryTakeForwardTo` (and `ReadAt`) leave out more of the frames nothing
  references: reading forward at steps, also those after the wanted frame and before the next time, which the frame
  threads and `ReadAhead` decode before the next time is asked for. `ReadAt` knows the next time; reading forward in
  mp4, mov and mkv guesses it after steps of one length, and where a guess was too late reads what it left out again
  from the keyframe before, so the frames are still those of decoding every packet. A picture a second of a 5-minute
  1080p H.264 clip with `ReadAhead = 4`: 95% of those frames left out instead of 60%, 13% less CPU.
