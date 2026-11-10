### New

- scenedetect 0.7.3 (issue #768), now the default compatibility mode (`PySceneDetectVersion.V0_7_3`, `--compat
  0.7.3`); 0.7.2 stays selectable (`V0_7_2`, `--compat 0.7.2`). Following a new scenedetect release changes default
  results, as README's "Versions" describes. Detection is 0.7.2's; the outputs move as scenedetect's did (CI checks
  both modes):
  - A run that reads to the end ends its last shot a frame after the last frame's own time (scenedetect #561): on
    variable frame rate video, or where frames start late, the end moves.
  - The scene list CSV's first row is "Timecode List:" also without cuts (0.7.2 left it blank).
  - `--save-edl` (`Timeline.Edl`) counts frames at the nominal rate at 23.976, 29.97 and 59.94 fps (24, 30 or 60 a
    timecode second, as non-drop-frame timecode does), so its timecodes no longer drift 3.6 s an hour;
    `--edl-start-timecode` is read the same way (#593).
  - `--save-otio` (`Timeline.Otio`): each clip's available range is the video's length instead of 1980 frames (#580).
  - `$VIDEO_NAME` of a URL is its path's last segment, without the query or fragment (#197).
- `Shots.Csv(shots, compatibility, ...)`: the scene list CSV as a given scenedetect release writes it;
  `Shots.Csv(shots, ...)` writes 0.7.3's.
- CLI: `--downscale 0` is the automatic factor, as scenedetect 0.7.3's `-d 0`.
- `PySceneDetectVersion.V0_7_1` is `[Obsolete]`: it still works, but CI no longer checks it against scenedetect 0.7.1,
  and the next major version removes it (docs/decisions/2026-10-31-retiring-scenedetect-modes.md).
