# #194 on v2: streams without the libraries (2026-10-13)

FrameReader.StreamInfo's constructor was internal, and ShotDetector lists streams from ffprobe when it runs the
executable (no libraries, or URLs). Asked: **a public StreamInfo constructor** (additive; ShotDetector fills it
from ffprobe, duration_ts and time_base included), so Streams works on every path, over no streams without the
libraries.
