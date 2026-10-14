# Issue #81's enhancements (2026-10-09)

Issue #81 asked for a fuller probe, audio decoding and analysis, encoding and waveform data. Asked which to put on the
backlog: **the fuller probe, spectral statistics via aspectralstats and waveform peaks (recommended), and audio filter
graphs**. Encoding (H.264, MP3) not chosen: x264 is GPL and can't go into the LGPL packages, and encoding makes it a
different product; SplitVideo's `SplitOptions.Args` already passes ffmpeg's -g, -keyint_min and -force_key_frames.
Reply on the issue now with the plan (recommended).
