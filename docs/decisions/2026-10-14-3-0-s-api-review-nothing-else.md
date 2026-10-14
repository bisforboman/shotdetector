# 3.0's API review: nothing else (2026-10-14)

Before 3.0.0, ShotDetector's public API (24 types) was reviewed for what only a major can change: the crop's two shapes
(`DetectionOptions.Crop` as corners, scenedetect's; `DetectedVideo.CropRegion` as X, Y, width, height), the flat detector
properties beside the `Detectors` list, times as scenedetect's strings, `Fps.FromFloat`. Asked: **none, ship 3.0 as is**
(over unifying the crop, keeping only the list, or the rename). A corners property beside `CropRegion` can come in a
3.x without breaking anything if it's wanted; typed time overloads too.
