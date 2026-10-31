### New

- `shotdetect --save-youtube-chapters <file>` and `Timeline.YouTubeChapters`: chapters for a YouTube video's
  description, a line each (`0:00 Chapter 1`), meeting YouTube's rules (first at 0:00, at least three, each at least
  10 s): shots merge until a chapter has run `--chapter-min-length` seconds (10 by default), each cut's time rounded up
  to the second (issue #648).
