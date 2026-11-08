### Fixed

- `shotdetect qc`: in a file whose video starts after its audio (a Matroska file with an offset, a fragmented MP4 with
  B-frames), the colour bars and the worst broadcast-range frame were timed from the video's first frame, while the
  tone, black and freeze spans count from the file's start, so the bars and the tone could miss each other. Black and
  freeze were also judged against a head and tail that much too early, so a span mid-programme could be listed as tail
  and pass. Every time now counts from the file's start, and the head and tail are the video's own.
