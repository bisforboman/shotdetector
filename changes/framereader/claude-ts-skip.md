### Faster

- `VideoFrameReader.TryReadForwardTo` and `TryTakeForwardTo` in MPEG-TS files: reading forward at steps of one
  length leaves out the frames nothing references after the wanted frame and before the next time, as in mp4, mov
  and mkv since 2.16.0. Where a guess was too late, it reads what it left out again from a keyframe it has read, by
  the keyframe's byte position: a TS seek by time lands a whole keyframe interval late. The frames are still those
  of decoding every packet.
