# Hardware decoding: merged as opt-in (2026-10-09)

Built (Windows, D3D11VA; frames copied back and laid out as the software decoder's yuv420p): identical pictures on the
user's GPU, about half the CPU, but 3.6x the wall time on the 12-minute film (every frame waits for the GPU and its
copy, as with ffmpeg's -hwaccel). Asked: **merge as opt-in (recommended)**, off by default and documented with these
numbers, over shelving it.
