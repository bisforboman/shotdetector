# The contact sheet (2026-10-31)

Issue #649: a contact sheet in the CLI, a grid of each shot's frames with its number, timecode and length, from the
same frames as `--save-images`. Our FFmpeg build has no drawtext filter, so labels on a PNG need a font drawn in C#;
a PDF can use its standard Helvetica and embed JPEGs as they are, with no new dependency. `--save-html` shares only
save-images' choice of frames with it: its page is scenedetect's, byte for byte, so nothing of its layout carries over.

## Format

### Choices

1. **Both, picked by the file's extension**: `.png` one image with labels in a small built-in bitmap font, `.pdf`
   pages with real text and the frames embedded as the JPEGs save-images writes.
2. PDF only.
3. PNG only.

### Answer

**1** (the owner, 2026-10-31).

## Layout

### Choices

1. **A row per shot**: its label (number, start timecode, length), then its images (`--num-images`, 3 by default)
   left to right at a fixed thumbnail width. Reads like the shot list and takes save-images' frames as they are.
2. A grid of shots, one middle frame each, N columns.
3. Both, through an option.

### Answer

**1** (the owner, 2026-10-31).
