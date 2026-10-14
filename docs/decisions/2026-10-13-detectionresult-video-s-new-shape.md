# DetectionResult.Video's new shape (2026-10-13)

For the 2.0 result record, asked: the name **DetectedVideo** (over AnalyzedVideo, VideoDetails); streams **dropped
from the result** (over probing eagerly: detection doesn't need them, and the old property getter probed or ran
ffprobe on first use); the analysed size as **ProcessedWidth/ProcessedHeight** (the CLI's "processed at"; Width and
Height now only ever mean the source). Members follow VideoInfo's names (FrameRate, not Fps; Deinterlaced,
DecodedInProcess, Streamed as past facts); FramePipeline isn't on it (the next 2.0 item hides it).
