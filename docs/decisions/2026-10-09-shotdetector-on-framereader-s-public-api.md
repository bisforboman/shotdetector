# ShotDetector on FrameReader's public API (2026-10-09)

ShotDetector reached into FrameReader for its BGR conversion (FrameDecoder's AVFrame* and on-demand deinterlaced
rows) and for a Stream with a prefix already read. Asked what the public API should give: **a `BgrConverter` class
(the current frame as ffmpeg's scale,format=bgr24, whole or only some rows; no FFmpeg types in the API;
recommended)**, not exposing the AVFrame* and a PrepareRows; and for the stream, **a prefixed-stream wrapper in
ShotDetector over the existing FrameDecoder(Stream) (recommended)**, not a prefix overload in FrameReader.
