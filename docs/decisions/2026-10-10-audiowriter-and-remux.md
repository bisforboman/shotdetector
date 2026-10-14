# AudioWriter and Remux (2026-10-10)

For the MP3/AAC/muxers work. Asked: **an `AudioWriter` class (Write(samples), Dispose finishes; codec from the
extension) (recommended)**, not a one-call transcode or both; **a static `Remux.Copy(input, output, options)` with
streams by type or index (recommended)**, not also a split-everything helper; and **bitexact as an option, off by
default (recommended)**, so files carry FFmpeg's usual encoder tags unless asked.
