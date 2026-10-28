# save-images in-process: WebP's encoder and the decoded-frame encoder's filter (2026-10-28)

The owner's choice of 2026-10-28: ShotDetector's `--save-images` in-process through FrameReader, byte-identical to the
files the ffmpeg executable of the same FFmpeg build writes. Two questions came up before building it. The first PR adds
the encoder that scales from a decoded frame (the backlog row of
[2026-10-27-image-encoder-input.md](2026-10-27-image-encoder-input.md), question 3). The second moves `Export.SaveImages`
onto it.

### Question 1

`Export.SaveImages` names no encoder for `.webp`, so ffmpeg picks the first one registered for the format, `libwebp_anim`.
That was the case with BtbN's 8.1.3 and Ubuntu's 6.1. `libwebp_anim` holds every frame and writes one animation at the end.
With two or more distinct frames, the one ffmpeg run writes only `000001.webp`, an animation, and save-images then fails
copying the missing second file. With one frame it writes an animated-encoder file (21592 bytes, against 10488 from
`-c:v libwebp`). So there are no files of today's to match for WebP. What should WebP do?

### Choices

1. **`-c:v libwebp` on the executable path too**: a fix (`### Fixed`). In-process gives what that writes.
2. **WebP stays on the executable only**, still broken, with a backlog row.
3. **In-process uses libwebp and the executable path stays as it is**, so the two paths write different files.

### Answer

**1. `-c:v libwebp` on the executable path too** (the owner, 2026-10-28). The changelog entry says save-images to WebP
with two or more images failed, confirmed through shotdetect itself.

### Question 2

Export's chain is `select=...,scale='round(iw*sar)':ih,setsar=1` at ffmpeg's default flags, then, for a size, one of
`scale=W:'trunc(ih*W/iw)'`, `scale='trunc(iw*H/ih)':H`, `scale=W:H` or `scale='round(iw*s)':'round(ih*s)'`, each with
`flags=bilinear`. That is two scale filters. Which of them converts to the encoder's format is lavfi's format negotiation
over the whole chain, so the same bytes need the same chain. The encoder's planned options (Width, Height and MaxWidth at
the default flags) can't express it. How should FrameReader's API carry it?

### Choices

1. **A public `Filter` string** (ffmpeg's `-vf` syntax) beside Width, Height and MaxWidth, refused together with them,
   between the buffer source and the encoder's format constraint. It is general, literally the command line's graph, and
   Export passes its own chain.
2. **Named options shaped for Export**: square pixels, scale flags, a scale factor, truncation for a one-sided size.
   That is more API, all of it ShotDetector's.
3. **Change Export's executable chain** to what the planned options give, so today's files change.

### Answer

**1. A public `Filter` string** (the owner, 2026-10-28): `ImageFilterOptions` with `Width`/`Height`, `MaxWidth` or
`Filter`, and `ImageEncoder.Encode(DecodedFrame, ...)`. Building it showed what else ffmpeg's command line does to a
decoded frame between decoding and encoding, all of it followed:

- The scale filter discards the frame's chroma siting (`in_chroma_loc` defaults to unknown, so swscale's centre). This is
  libavfilter, not fftools, so the same graph does it in-process.
- The encoder gets the decoder's bits per raw sample only when the graph is metadata-only (`graph_is_meta`). A 10-bit
  source's PNG otherwise gains an `sBIT` chunk that ffmpeg's doesn't have.
