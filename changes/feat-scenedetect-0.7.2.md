### New

- scenedetect 0.7.2 (issue #177), now the default compatibility mode (`PySceneDetectVersion.V0_7_2`); 0.7.1 stays
  selectable (`V0_7_1`, `--compat 0.7.1`). Following a new scenedetect release changes default results, as README's
  "Versions" describes. Each mode gives its release's cuts, scene lists and stats (CI checks both):
  - detect-hist defaults to threshold 0.20 and 128 bins, detect-hash to 0.35 and size 8 (0.7.1: 0.05/256 and
    0.395/16). `DetectionOptions.Bins` and `HashSize` default to 0 now, meaning the release's default.
  - A minimum length given in frames (as the CLI passes it) is compared as a frame count in the content detector's
    flash filter, so an exact gap of that many frames counts (scenedetect #577).
  - The adaptive detector measures the minimum length from the frame it cuts at (scenedetect #408).
  - detect-threshold ignores fade-outs shorter than `MinOutLength` (new; CLI `--min-out-length`), 0.1 s by default
    as in scenedetect's CLI.
- `Timeline.Keyframes` and CLI `--save-keyframes <file>`: scenedetect 0.7.2's save-keyframes (Aegisub's keyframe
  format v1).
