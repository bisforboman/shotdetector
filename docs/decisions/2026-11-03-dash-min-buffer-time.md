# The DASH MPD's minBufferTime (2026-11-03)

Issue #717: `Remux` writing MPEG-DASH (`Container = "dash"`) gives the MPD the dash muxer's `minBufferTime`, PT12.5S
with `seg_duration=6`, where Shaka Packager writes 2 s for the same input. Players buffer at least `minBufferTime`
before they start (Shaka Player takes the larger of it and its own rebuffering goal), so the in-process output starts
later than Shaka's and isn't a drop-in replacement. The issue asks for a setting, defaulting to today's value.

## Question

Investigate, then build? The owner chose (2026-11-03): **investigate, then build** a `TimeSpan? MinBufferTime` on
`RemuxOptions`, null giving today's output byte for byte; a muxer option if FFmpeg has one, otherwise a rewrite of the
final MPD; and a clear failure for a mode where that can't work.

## What FFmpeg 8.1's dash muxer does (libavformat/dashenc.c, the version our native build uses)

- `write_manifest` writes `minBufferTime` as `c->ldash && c->max_gop_size ? c->max_gop_size : c->last_duration * 2`:
  twice the duration of the last segment that ended at a keyframe (the video stream's, when there is video), or with
  `ldash` the longest GOP. If no segment ended before the trailer (input shorter than one segment), `last_duration`
  is the first stream's duration. `write_time` writes it as `PT[hH][mM]s.fS`, cut (not rounded) to tenths: 6.25 s
  segments give PT12.5S; 0.96 s segments give PT1.9S.
- **No option sets it.** The muxer's options (seg_duration, frag_duration, window_size, streaming, ldash,
  target_latency, min/max_playback_rate, update_period, ...) change other attributes or the value only indirectly
  (`seg_duration` moves `last_duration`; `ldash` switches to the GOP).
- **The MPD is rewritten during muxing:** after every segment (`dash_flush`, unless `streaming`), at the start of
  every segment in `streaming` mode, and once more in the trailer with `final = 1`, which gives the static MPD that
  stays. Each write goes to `<mpd>.tmp` and is renamed over the MPD for a file output (a non-file protocol is written
  in place). `remove_at_exit` deletes the MPD and the segments after the trailer.

## Choice: rewrite the final MPD

With no option, the value goes in after `av_write_trailer`: the MPD element's `minBufferTime` attribute is replaced and
nothing else, as `DashNames` and Widevine's signalling already rewrite the MPD after muxing. The value is written in
the muxer's own shape (`PT2.0S`, `PT1M30.5S`), the fraction to the tick instead of cut to tenths.

A rewrite reaches only the final MPD, so `MinBufferTime` is refused (`ArgumentException`, before anything is written)
where that would mislead:

- `streaming`, `ldash` or `window_size` set (non-zero): these are for an MPD players read while it is written, and
  every MPD they read would carry the muxer's value;
- `remove_at_exit`: the MPD is gone before it could be set;
- an output that isn't a local file (a protocol such as `http://`): there is no file to rewrite;
- output other than DASH.

Today's behaviour is untouched when `MinBufferTime` is null: no rewrite, the files ffmpeg's `-f dash` writes, byte for
byte (RemuxTests).

The alternative, a patch to our FFmpeg build adding a `min_buffer_time` option, would also cover the live modes but
makes a FrameReader.Native release and a patch to carry across FFmpeg versions, for modes `Remux` (a file in, read to
the end) doesn't aim at. If live DASH output becomes a need, that's the way, and FFmpeg upstream the place.
