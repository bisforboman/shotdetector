### Fixed

- Two encodes joined (timestamps that start over, a resolution change): ShotDetector now gives scenedetect's results
  (it found 2 cuts where scenedetect finds 7 on the test file, and in-process decoding failed). Frames are labelled by
  their time as scenedetect labels them, so cuts that come out of order are sorted and a later frame's stats replace
  an earlier one's at the same time, metric by metric; frames of a new size are scaled to the stream's size in the one
  conversion, as OpenCV does. Damaged files are now compared with scenedetect in CI.
- Stats where a timestamp repeats (two encodes joined, duplicated timestamps): the threshold detector keeps the
  average already stored at that time, for its stats and its cuts, as scenedetect's does when writing stats.

### New

- FrameReader: `BgrConverter.KeepFirstSize`, to scale frames of another size to the first one's.
