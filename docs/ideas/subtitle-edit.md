# Subtitle Edit's shot changes: scoping note (issue #633)

Status: **scoping only**, 2026-10-31. No feature code. The idea (issue #633): offer ShotDetector to
[Subtitle Edit](https://github.com/SubtitleEdit/subtitleedit) as the engine behind **Video → Generate/import shot
changes**, which today runs FFmpeg's `scene` filter with a sensitivity slider. Subtitlers snap cue in and out times to
shot changes, so better cuts help them every day. This note covers what exists, how Subtitle Edit would take an engine,
the gap, a first slice, the risks, and a verdict with the questions only the owner can answer.

Subtitle Edit was read from its own source, cloned on 2026-10-31:

- **SE5** (Avalonia, cross-platform; now the main repository): `SubtitleEdit/subtitleedit` `main` at `8c6fc9d`
  (2026-10-31). The separate `niksedk/subtitleedit-avalonia` repository is archived; its README says development moved
  to `SubtitleEdit/subtitleedit`.
- **SE4** (WinForms, .NET Framework 4.8): the same repository's `se4-legacy` branch at `96e6d30` (2026-10-10).

Paths below are relative to those checkouts.

## 1. What already exists

### On our side

- **Shot lists**: `ShotDetection.Detect`/`DetectAsync`/`DetectStreamAsync` (`src/ShotDetector/ShotDetection.cs`).
  They return `DetectionResult.Cuts` (`IReadOnlyList<FrameTime>`, with `Seconds`, `FrameNum` and `SecondsText()`,
  `src/ShotDetector/FrameTime.cs`) and `Shots`. The options include `Progress` (`IProgress<DetectionProgress>`),
  `OnShot`, `Warnings`, a `CancellationToken`, `FfmpegDirectory` and `Decoder`. All five detectors match scenedetect
  0.7.2.
- **CLI** (`src/ShotDetector.Cli/Program.cs`): `--csv` and `--json` write the shot list. The timeline exports
  (`src/ShotDetector/Timeline.cs`) are `--save-edl`, `--save-fcp`, `--save-otio`, `--save-qp` and
  `--save-keyframes`. Each matches scenedetect's file byte for byte (the EDL's comment line aside).
  `--save-keyframes` writes Aegisub's keyframe format v1: frame numbers, one per line, after a `# keyframe format v1`
  and `fps 0` header.
- **Release binaries**: Native AOT `shotdetect`, each with our FFmpeg 8 libraries next to it. They exist for
  `linux-x64`, `linux-arm64`, `linux-musl-x64`, `win-x64` and `osx-arm64` (`.github/workflows/release.yml`, the binary
  matrix). They need no .NET, no ffmpeg and no ffprobe for files.
- **FrameReader** (MIT) and **FrameReader.Native.\<rid\>** (our FFmpeg 8.1 build, LGPL): in-process decoding. They
  carry the same five RIDs and use `FFmpeg.AutoGen` `[8.1.0, 9.0.0)` (`Directory.Packages.props`). FFmpeg 9's libraries
  don't load in-process (README, "FFmpeg 9's libraries (libavcodec 63) don't load in-process"). The move to 9 is a
  *Waiting* backlog row, waiting for opencv-python's wheels.
- **ShotDetector.FastYuv** (LGPL-2.1): a faster BGR conversion with identical results. Nothing here needs it.
- **shotdetector-web's core** (`github.com/bisforboman/shotdetector-web`, read at `06aad0f`):
  `src/ShotDetectorWeb.Core` decodes with ManagedCodecs, with no FFmpeg at all. It only covers 8-bit 4:2:0 H.264 in
  MP4 and MJPEG in AVI (its README, "Supported so far"). Subtitlers' sources (MKV, TS, HEVC, interlaced broadcast) are
  mostly outside that, so it **isn't relevant** here, apart from showing that `Detect(YuvVideo, frames)` works with
  another decoder's frames.

### On Subtitle Edit's side (SE5)

- **Generating**: `src/ui/Features/Video/ShotChanges/ShotChangesViewModel.cs`, `GenerateShotChangesFfmpeg`. It runs the
  ffmpeg executable (`FfmpegGenerator.GetProcess`) with the format string
  `Se.Settings.Video.ShowChangesFFmpegArguments`, whose default is
  `-i "{0}" -vf "select=gt(scene\,{1}),showinfo" -threads 0 -f null -` (`src/ui/Logic/Config/SeVideo.cs:186`).
  `{1}` is the slider value: 0.05 to 0.95, default 0.4 (`ShotChangesWindow.cs:117-121`, `SeWaveform.cs:166`). The
  view model then reads every `pts_time:` from showinfo's log lines (`TimeRegex`) and drops times of 0.2 s or less.
  When the user opts in (`ShotChangesAutoGenerate`, off by default), the same command also runs silently when a video
  opens, at below-normal priority (`src/ui/Features/Main/MainViewModel.cs`, `ExtractShotChanges`).
- **Storage**: a `.shotchanges` file per video in `Se.ShotChangesFolder` (`<data>/ShotChanges`), named by the movie
  hash. It holds one time in seconds per line, invariant culture (`src/ui/Logic/Media/ShotChangesHelper.cs`, `ToText`,
  `SaveShotChanges`). The waveform, "extend to shot change", the Netflix checks and the beautifier read that list.
- **Importing** (`ShotChangesViewModel.LoadTextFile`): Matroska chapter XML, EZTitles shot-change XML (frame numbers),
  two JSON shapes (`frame_time` entries, or a `shotchanges` array of seconds), and plain text, one value per line. The
  user picks the unit for plain text: seconds, frames (divided by the video's frame rate), milliseconds, or
  `HH:MM:SS.ms`. Seconds is chosen automatically when the text contains a `.`.
- **FFmpeg**: SE downloads a **static ffmpeg executable only, with no ffprobe**
  (`src/ui/Logic/Download/DownloadHashManager.cs:508-510`: `ffmpeg902.zip` for Windows, `ffmpeg90arm.zip`,
  `ffmpeg80intel.zip`). It probes with ffmpeg itself (`FfmpegMediaInfo2`). Its own video player loads **FFmpeg 9**
  shared libraries through `FFmpeg.AutoGen` **9.0.1.1** (`src/ui/UI.csproj:34`,
  `src/ui/Logic/VideoPlayers/Ffmpeg/FfmpegLibraries.cs`: `MajorVersion = "9.0"`). It downloads them only on Windows x64
  (`FfmpegLibsDownloadService.cs`).
- **Build**: `net10.0`, published self-contained with `PublishSingleFile=true`, without trimming or AOT
  (`.github/workflows/build-ui.yml`). RIDs: `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`,
  `linux-arm64`.

### SE4

It has the same feature. `src/ui/Logic/ShotChangesGenerator.cs:54` runs the same `select=gt(scene\,…),showinfo`
command, and `src/ui/Forms/ShotChanges/ImportShotChanges.cs` imports the same file kinds. It is maintenance-only
(`se4-legacy`).

**So our existing outputs can reach SE today in one way only**: `--save-keyframes`, imported with the unit set to
*Frames* by hand. The two header lines then fail to parse and are skipped, and the `0` line becomes a cut at 0 s. This
is fine at a constant frame rate but wrong on VFR, because SE divides frame numbers by one rate. Neither our CSV nor our
JSON is a shape SE reads.

## 2. The external integration point

SE has **no engine interface for shot changes**. The only seam is the hard-coded ffmpeg command above. What SE does
have:

- **SE5 plugins** (`docs/plugin.md`, `src/ui/Logic/Plugins/`, contract `apiVersion: 1`). A plugin is a standalone
  executable listed in a `plugin.json` manifest. SE writes `request.json` (the subtitle, `videoFileName`, `frameRate`,
  `videoDurationSeconds`, …), starts the plugin with that file's path as its only argument, and reads `response.json`.
  The response can only carry `status`, `message`, `subtitle`, `settings`, `settingsVersion` and `undoDescription`
  (`PluginResponse.cs`). It **can't return shot changes**, and the request doesn't send the current ones. On `ok`, SE
  replaces the subtitle. Plugins appear only in a Plugins menu that is hidden by default.
- **SE4 plugins**: in-process .NET Framework DLLs. SE finds `Name`, `Text`, `Version`, `ActionType` and `DoAction` by
  reflection (`src/ui/Forms/Main.cs:27196`, `GetPropertiesAndDoAction`), and `DoAction` returns the new subtitle as a
  string. That also can't carry shot changes. It is also net48, and our packages are net10.
- **Downloaded external engines**: this is SE's own pattern for heavy tools. Whisper variants, TTS engines, Tesseract,
  yt-dlp and ffmpeg itself are downloaded on demand from pinned URLs, with SHA-256 hashes in
  `DownloadHashManager.cs`. SE then runs them as processes and parses their output. This is the closest fit for us.

Neither plugin API is a way in without changing SE. Any integration that writes SE's shot-change list needs either
**the user importing a file**, or **a change in SE**.

## 3. The gap, and a first slice

What's missing, by route:

| Route | Works today? | What's missing |
|---|---|---|
| User runs `shotdetect`, then imports in SE | Only via `--save-keyframes` with *Frames*, CFR only | An output in SE's own `.shotchanges` format |
| SE runs our release binary (like its Whisper engines) | No | An SE change: engine choice, downloader entry with hashes, output parsing; on our side progress SE can read (the CLI prints progress only to a terminal, `Program.cs:603`), and binaries for `win-arm64` and `osx-x64` |
| SE references the ShotDetector NuGet package | No | In-process: blocked by FFmpeg 8 vs 9 (one `FFmpeg.AutoGen` per process; SE's 9.0.1.1 wins over our `<9` range with NU1608, and FrameReader's 8.x bindings then run against 9's), until our FFmpeg 9 move. Executable path: SE ships no ffprobe. |
| SE5 plugin | No | A new field in SE's plugin contract (response `shotChanges`), i.e. an SE change anyway |

**First slice (one PR, here, no SE change): `--save-shotchanges <file>` and `Timeline.ShotChanges(result)`.** It writes
each cut's time in seconds, one per line, invariant culture, in the format of SE's `ShotChangesHelper.ToText`. The
times always keep a `.` (`SecondsText()`, e.g. `12.000`), because SE only picks *Seconds* automatically when it sees
one. The slice would also:

- leave out the 0 s line that `--save-keyframes` has, and document the file;
- test it on the VFR samples, checking that each line is the cut frame's presentation time (the time SE's player
  seeks to), not a frame number divided by a rate;
- add a mutation line, if a scenedetect quirk is involved (none is expected: the format is SE's, not scenedetect's).

Users could then run `shotdetect -i ep01.mkv --save-shotchanges ep01.shotchanges` and use SE's **Import shot changes**
tab, on SE4 and SE5 alike, with nothing for the SE maintainer to review. It is also what any later SE-side integration
would parse. This is a new output that scenedetect doesn't have, so README says so next to the timeline exports.

**Then, in order (estimates in PRs):**

1. `--save-shotchanges`: **1 PR** (above).
2. Machine-readable progress when stderr is redirected (for example `--progress-json`, one JSON line every 10 %):
   **1 PR**. SE needs it for its progress bar, as it already does with ffmpeg's `-progress`.
3. A comparison against `select=gt(scene,0.4)` on real subtitle material: **1 PR** for a tool and a report. The issue's
   "cuts missed, flashes wrongly flagged" needs **hand-labelled ground truth**, which we don't have. That is a question
   for the owner below.
4. Release binaries for `win-arm64` and `osx-x64`, so SE's six RIDs are covered: **1-2 PRs**. That means the native
   FFmpeg build on two more targets, so it is a FrameReader release (CLAUDE.md, release policy).
5. Outside this repo: a discussion on SubtitleEdit/subtitleedit with step 3's numbers. If the maintainer wants it, **1
   SE PR**: an engine choice (FFmpeg / ShotDetector) in the Generate tab, a `DownloadHashManager` entry for our binary,
   and parsing of step 1's file and step 2's progress. SE's AI guidelines
   (`.github/skills/subtitleedit-ai-contributor-guidelines/SKILL.md`) also require a Copilot review loop to be finished
   before merge.

In total: **3-5 PRs here plus 1 in SE**. Only step 1 is needed for subtitlers to benefit.

## 4. Risks

- **Licensing.** SE5 (`main`) is **MIT** (`LICENSE`, "Copyright (c) 2026 Nikolaj Olsson"). SE4 (`se4-legacy`) is
  **GPL-3.0** (`LICENSE.txt`). The issue says "GPL-3.0", which is out of date for SE5. ShotDetector and FrameReader are
  MIT, compatible with both. Our FFmpeg libraries (LGPL) ship as separate shared libraries next to our binary. SE
  already distributes LGPL FFmpeg libraries and libmpv the same way, so the binary route raises nothing new.
  **FastYuv (LGPL-2.1) is the one trap.** SE publishes `PublishSingleFile`, which would fold a managed LGPL DLL into
  `SubtitleEdit.exe`, where users can't replace it. Any NuGet route must leave FastYuv out, or exclude it from the
  single file. The release binaries don't include FastYuv, so the recommended route avoids this.
- **Whether the maintainer would accept it.** SE is very active, and outside PRs do get merged. Since 2026-05-01 the
  history shows thousands of merges from the maintainer's own branches, plus merged PRs from at least 14 outside
  accounts (e.g. 102 from `mjuhasz`, 95 from `ivandrofly`). Shot changes are being worked on right now: sorting,
  export, progress, overlap fixes, all in October 2026. But the maintainer's guidelines say: "Do not add
  configurability, extension points, dependencies … unless the issue clearly needs them". A NuGet dependency that
  brings a second FFmpeg major version is unlikely to pass. An optional downloaded engine fits what SE already does.
  It would still need numbers showing it beats `scene` on subtitlers' material. Hence the discussion first, with
  numbers (step 3), as the issue says. The plain-file route (slice 1) needs nobody's acceptance.
- **Runtime.**
  - *Natives per RID*: our five RIDs cover four of SE's six. `win-arm64` and `osx-x64` are missing (step 4). Until
    then SE would fall back to ffmpeg on those.
  - *FFmpeg major version*: one `FFmpeg.AutoGen` per process, ours 8 and SE's 9. Out-of-process, this doesn't matter.
  - *AOT and trimming*: SE uses neither. Our packages are `IsAotCompatible`, and the binary is AOT already.
  - *ffprobe*: SE doesn't ship it, and our executable path needs it. Our binaries don't need it, because they decode
    in-process with our libraries.
  - *macOS signing*: SE's `.dmg` is signed and notarized (its README, v5.2.0), but our macOS binary isn't (our README).
    A file SE downloads itself with `HttpClient` gets no quarantine flag, so Gatekeeper shouldn't stop it, but this
    needs checking on a Mac.
  - *Speed*: SE runs `scene` with `-threads 0` across the whole file. Our `Detect` chunks long files across cores, so
    run time should be compared in step 3, not claimed. The docs don't quote benchmark numbers.
  - *Times*: SE drops cuts at 0.2 s or less, and snaps cues to frames by time. Slice 1's VFR test covers the times.
- **The slider.** SE's 0.05-0.95 `scene` sensitivity has no direct counterpart; adaptive's threshold is 3 by default,
  and higher means fewer cuts in both. A mapping is a design choice for SE's maintainer. It isn't ours, and isn't
  needed for slice 1.

## 5. Verdict

**Needs the owner's decision.** Recommendation: **do slice 1** (`--save-shotchanges`, 1 PR). It is useful to SE4 and
SE5 users at once and commits us to nothing outside this repo. **Park the SE-side integration** until the owner answers
the questions below and a comparison (step 3) shows a gain worth proposing.

Questions for the owner (not asked here):

1. Should we contribute to Subtitle Edit at all (a discussion, then possibly a PR there), or only ship a file SE
   imports?
2. Is a non-scenedetect output (`--save-shotchanges`, SE's format) in scope for the CLI and `Timeline`, next to the
   scenedetect-identical exports?
3. For the comparison: hand-label the cuts of a few real subtitle jobs (who does it, on which material, and is the
   material shareable), or accept a proxy such as agreement between detectors?
4. Is SE worth building and releasing binaries for `win-arm64` and `osx-x64` (a FrameReader release)?
5. If SE wants it in-process later: is that a reason to move to FFmpeg 9 before OpenCV does? That would decouple
   FrameReader's FFmpeg from scenedetect's. The current decision (backlog, Waiting) is to wait for OpenCV.
