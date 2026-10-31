# Idea: a Jellyfin plugin for shot-based chapters and black/fade segments

Scoping note for issue #634 (owner's request, 2026-10-31). No feature code. Jellyfin facts were read from the source
of the tags named (shallow clones and raw files, 2026-10-31), not from memory; the paths below are in
`github.com/jellyfin/jellyfin` unless another repository is named.

**Verdict: needs the owner's decision.** The technical fit is good: Jellyfin 12 is on net10.0, as we are, and its
Docker image ships FFmpeg 8.1. The open questions are where the plugin lives (its own GPL-3.0 repository or a
contribution to Intro Skipper) and whether to support only Jellyfin 12. The first slice is chapters only, in a
new plugin repository: one PR (see "First slice"). The decision questions are at the end.

## 0. What changed since the issue was filed

The issue names Jellyfin 10.10. Jellyfin has moved on since then: the tags go `v10.10.7`, `v10.11.11`, then `v12.0`
… `v12.4` (committed 2026-10-26), and NuGet's `Jellyfin.Controller`/`Jellyfin.Model` are at 12.4.0. Intro Skipper's
default branch (`12.0`) requires Jellyfin 12.0.0 or newer. The target frameworks of `MediaBrowser.Controller.csproj`
are: 10.10 net8.0, 10.11 net9.0, 12.x **net10.0**.

ShotDetector, FrameReader and FastYuv target `net10.0` only (`Directory.Build.props:3`). So Jellyfin 12 can load them
as they are. 10.10 and 10.11 would need us to build for net8.0/net9.0 as well, which this repository doesn't do. A
plugin started today targets 12.x.

## 1. What already exists

**ShotDetector** (`src/ShotDetector`)
- `ShotDetection.Detect`/`DetectAsync(string path, DetectionOptions)` returns `DetectionResult`
  (`ShotDetection.cs:600`): `Shots`, `Cuts` (`FrameTime`, which converts to seconds and so to Jellyfin's ticks),
  `Stats`, `Warnings`.
  - Each detector is chosen with `DetectorKind` (`ShotDetection.cs:29`). Several can run in one pass through
    `DetectionOptions.Detectors`/`DetectorSettings` (`ShotDetection.cs:171`).
  - `CancellationToken` is honoured, which a scheduled task needs.
- Fades: `ThresholdDetector` (`Detectors.cs:195`) is scenedetect's detect-threshold (FLOOR). It cuts between a fade-out
  and the next fade-in, and with `CollectStats` it records each frame's `average_rgb` (`Detectors.cs:243`).
  - It reports a cut, not a span. A black span (fade-out frame to fade-in frame) is in the stats, as the frames whose
    `average_rgb` is under the threshold, but no public API returns it yet.
- Cut strength for picking chapter points: `content_val` per frame (`ContentScorer.cs:110`), and the adaptive ratio
  (`Detectors.cs:133`), through `Stats.Get(frame, key)` (`Stats.cs:60`) when `CollectStats` is on.
  - `Shot` itself (`Shots.cs:9`) carries no score.
- Cost controls: `FrameSkip` (`ShotDetection.cs:345`) and `Downscale` (`ShotDetection.cs:497`).
  - Parallel chunks are on by default from 4 CPUs (`DetectionMode`, `ShotDetection.cs:61`;
    docs/parallel-detection.md). A server plugin may want `Sequential` to stay light while people are watching.
- `DetectionOptions.FfmpegDirectory` (`ShotDetection.cs:477`) points at a folder with ffmpeg/ffprobe, and at
  FFmpeg's shared libraries for `VideoDecoder.InProcess`.
  - `VideoDecoder.Auto` (`ShotDetection.cs:132`) falls back to the executable when the libraries don't load.
  - So jellyfin-ffmpeg can be used without shipping anything native.

**FrameReader** (`src/FrameReader`)
- `FFmpegLibraries.Load(directory)` (`FFmpegLibraries.cs:37`) loads FFmpeg 8's shared libraries from one folder, once
  per process; `CanLoad` checks first.
- `MediaProbe`/`ShotDetection.Probe` give the headers (duration, streams).
- No public API reads a file's existing chapters. Remux only copies them (`Remux.cs:414`). The plugin doesn't need one
  either: Jellyfin already knows a file's chapters through `IChapterManager`.

**FastYuv** (`src/ShotDetector.FastYuv`, LGPL-2.1): an optional faster yuv420p→BGR path. A first slice doesn't need it.

**FrameReader.Native.<rid>** (`src/FrameReader.Native`): our FFmpeg 8.1.3 build for win-x64, linux-x64, linux-arm64,
linux-musl-x64 and osx-arm64 (`.github/workflows/native.yml`). The libraries are packed under `runtimes/<rid>/native/`.

**shotdetector-web's picture measures** (`PictureMeter`, black frames): the issue says they live in shotdetector-web's
core. This session couldn't read that repository (not attached), so that is **unverified** here. Without them, black
spans come from ThresholdDetector's `average_rgb` (above): the same measure scenedetect uses for fades.

## 2. Jellyfin's integration points (verified in source)

**Media segments: `IMediaSegmentProvider`**
- Where it is, per version:
  - 10.10.7: `MediaBrowser.Controller/MediaSegements/IMediaSegmentProvider.cs` (the folder name is misspelled
    upstream).
  - 10.11: `MediaBrowser.Controller/MediaSegments/`, namespace `MediaBrowser.Controller.MediaSegments`.
- Members:
  - `string Name`
  - `Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest, CancellationToken)`
  - `ValueTask<bool> Supports(BaseItem)`
  - 12.4 adds `Task CleanupExtractedData(Guid itemId, CancellationToken)`.
- `MediaSegmentGenerationRequest` holds no path, so the provider looks the item up through `ILibraryManager`:
  - 10.10: `Guid ItemId` only.
  - 10.11 and 12: also `ExistingSegments`.
- `MediaSegmentDto` has `Id`, `ItemId`, `Type`, `StartTicks` and `EndTicks`.
- `MediaSegmentType` values: Unknown=0, Commercial=1, Preview=2, Recap=3, Outro=4, Intro=5.
  - There is no "black" or "fade" type: a black span would be `Unknown`, and credits would be `Outro`.
- Registration: `MediaSegmentManager` (`Jellyfin.Server.Implementations/MediaSegments/MediaSegmentManager.cs`) takes
  `IEnumerable<IMediaSegmentProvider>` from DI. A plugin adds its provider in an `IPluginServiceRegistrator`, as Intro
  Skipper does (`IntroSkipper/PluginServiceRegistrator.cs`).
  - Each library can disable providers and order them (`LibraryOptions.DisabledMediaSegmentProviders`,
    `MediaSegmentProviderOrder`).
- When providers run: only from the scheduled task "Media Segment Scan"
  (`Emby.Server.Implementations/ScheduledTasks/Tasks/MediaSegmentExtractionTask.cs`, every 12 h by default), for
  local Episode, Movie, Audio and AudioBook items.

**Chapters**
- How a plugin saves them, per version:
  - 10.10: `IChapterManager.SaveChapters(Guid, IReadOnlyList<ChapterInfo>)`.
  - 10.11: `SaveChapters(Video, …)`, plus `IChapterRepository`.
  - 12.4: `SaveChapters(BaseItem, …)` and a new `Supports(BaseItem)`.
- `SaveChapters` drops chapters at or past the item's `RunTimeTicks` (`Emby.Server.Implementations/Chapters/ChapterManager.cs`).
- `ChapterInfo` (`MediaBrowser.Model/Entities/ChapterInfo.cs`) has `StartPositionTicks`, `Name`, `ImagePath`,
  `ImageDateModified` and `ImageTag`.
- Chapter images: the daily "Extract Chapter Images" task (`ChapterImagesTask.cs`) reads the stored chapters, so it
  would also make images for chapters a plugin saved.
  - Only when the library has `EnableChapterImageExtraction` on.
  - Skipped when the average chapter length is under 1 s.
- **Overwrite:** a metadata probe in Default or FullRefresh mode re-saves ffprobe's chapters
  (`MediaBrowser.Providers/MediaInfo/FFProbeVideoInfo.cs`), or dummy chapters when `DummyChapterDuration` > 0.
  - A probe happens when the file or its external subtitle/audio files change, or on a forced refresh.
  - So the plugin's task must run again after a refresh, idempotently.
- Precedent: intro-skipper/jellyfin-plugin-ms-chapter writes chapters (from segments) with
  `IChapterManager.SaveChapters(video, …)`, on net10.0.

**Packaging and loading**
- Plugin template (`jellyfin/jellyfin-plugin-template`, master): still on 10.11, so it needs updating to 12.x by hand.
  - It uses net9.0, `Jellyfin.Controller`/`Model` with `ExcludeAssets=runtime`, and `build.yaml`
    (`targetAbi`, `framework`, `artifacts`).
- `meta.json` fields are in `MediaBrowser.Common/Plugins/PluginManifest.cs`; `assemblies` is the list of DLLs to load.
- Loader (`Emby.Server.Implementations/Plugins/PluginManager.cs`, `PluginLoadContext.cs`; the same from 10.10 to 12.4):
  - Each plugin gets one collectible `AssemblyLoadContext` with an `AssemblyDependencyResolver` for its folder.
  - **`LoadUnmanagedDll` isn't overridden**, so `runtimes/<rid>/native/` isn't resolved.
  - Without `assemblies` in `meta.json`, every `*.dll` in the folder is loaded as managed, recursively, so a
    Windows native DLL marks the plugin Malfunctioned.
- Precedent for native libraries: luisquintanilla/jellyfin-semantic-discovery (`docs/compatibility.md`) lists
  `assemblies`, puts `libonnxruntime.so` next to the plugin DLL and installs `NativeLibrary.SetDllImportResolver`.
- FFmpeg:
  - `IMediaEncoder.EncoderPath`/`ProbePath` (`MediaBrowser.Controller/MediaEncoding/IMediaEncoder.cs`); Intro Skipper
    uses `EncodingOptions.EncoderAppPathDisplay`.
  - The Docker image (`jellyfin/jellyfin-packaging`, `docker/Dockerfile`) installs `jellyfin-ffmpeg8` at
    `/usr/lib/jellyfin-ffmpeg/ffmpeg`.
  - jellyfin-ffmpeg 8.1.3 is built `--enable-shared --enable-gpl --enable-version3` (GPLv3), and its `.deb` puts
    `libavcodec.so*` and the other libraries in `/usr/lib/jellyfin-ffmpeg/lib`.

**Intro Skipper** (intro-skipper/intro-skipper, branch `12.0`, b073a73, 2026-10-30)
- GPL-3.0, about 2.9k stars, net10.0 on Jellyfin.Controller 12.2.0.
- Implements `IMediaSegmentProvider` (`IntroSkipper/Providers/SegmentProvider.cs`).
- Detection runs through an internal `IMediaFileAnalyzer`, with these analyzers: Chromaprint, BlackFrame, Chapter,
  Subtitle, and credits analyzers in `Analyzers/Credits/`.
- **It already finds credits visually**: black rolls and card runs from a keyframe scan with ffmpeg's blackframe and
  blackdetect (`docs/credits.md`). It uses no scene-change detection.
- No issue asks for shot detection. The nearest are #1147 (BlackFrame credits catch story scenes, open) and #1121
  (coloured animated credits missed, closed).
- No CONTRIBUTING file; the contributor notes are in `AGENTS.md`, `CLAUDE.md` and `CONTEXT.md`.

**Existing chapter plugins:** none makes chapters from scene detection. A GitHub search found:
- jellyfin/jellyfin-plugin-chapter-segments: chapters → segments.
- intro-skipper/jellyfin-plugin-ms-chapter: segments → chapters.
- Viperinius/jellyfin-plugin-nfo-chapters: chapters from NFO files.
- superuser404notfound/jellyfin-plugin-chapter-rules: segments from chapters.

The official plugin manifest (repo.jellyfin.org) was outside this session's network, so it is unchecked.

## 3. The gap, and a first slice

**Gap**
1. **The plugin itself** (Jellyfin side): scheduled task, configuration page, `IPluginServiceRegistrator`, and the
   `meta.json`/`build.yaml`.
   - It lives in a GPL-3.0 repository (§4), not here.
2. **Chapter-point picking**, which nothing does yet. From `Cuts` plus a strength per cut (`content_val` or the
   adaptive ratio from `Stats`): the strongest cut in each window, at most one every N minutes, none in the first or
   last minute.
   - The plugin can do this at first. If other users want it, it later becomes a ShotDetector API
     (`Shots.PickChapters`?), with the "Better to have a correct baseline" bar: deterministic and tested.
3. **Black/fade spans as spans**: ThresholdDetector only reports cuts. Something has to return the frame ranges under
   the threshold, either from stats (in the plugin) or as a new public result (a ShotDetector release).
   - Credits are a harder problem: Intro Skipper already handles them, and black spans alone don't mark credits.
4. **In-process decoding inside Jellyfin** (optional). Two ways:
   - Point `FfmpegDirectory` at `/usr/lib/jellyfin-ffmpeg/lib`. These are patched GPL builds, untested against
     FFmpeg.AutoGen 8.x; results would need checking against our own build.
   - Ship FrameReader.Native's libraries flat in the plugin folder, with `assemblies` set and a resolver, as
     jellyfin-semantic-discovery does.

   The first slice uses the executable instead (`Decoder = FfmpegProcess`, `FfmpegDirectory` = jellyfin-ffmpeg's
   folder), so it ships nothing native.

**First slice (one PR, in a new plugin repository):** "Generate chapters from shots".
- A scheduled task for Movies and Episodes with no chapters, or only Jellyfin's dummy chapters.
- For each item it runs `ShotDetection.DetectAsync`:
  - `Detector = Adaptive`, `CollectStats`, `FrameSkip` and `Downscale` from the settings.
  - `Mode = Sequential`.
  - Decoding through jellyfin-ffmpeg's executable.
- It picks chapter points (as in the gap above) and saves them with `IChapterManager.SaveChapters`, named "Chapter N".
- Targets Jellyfin 12.4 / net10.0. Tests cover the picker on a synthetic cut list.
- Chapter images then come from Jellyfin's own task.

**Size:** about 1 PR for the slice, after a repository-setup PR (template on 12.x, build.yaml, CI, licence).

Later work:
- Segments from fade/black spans: 1-2 PRs (one of them in ShotDetector if the spans become public API).
- Bundling the native libraries: 1 PR.
- Publishing a plugin repository manifest: 1 PR.

About 4-6 PRs in all, plus one ShotDetector PR for each API the plugin would otherwise copy.

## 4. Risks

**Licensing**
- Jellyfin's server LICENSE is GPL-2.0, and the `Jellyfin.Controller` package declares `GPL-3.0-only`. The template's
  README says a plugin built against those packages is GPLv3 (or a permissive licence that can be linked with GPLv3);
  proprietary plugins are not permitted.
- So the plugin repository is GPL-3.0. Our libraries stay as they are:
  - MIT (ShotDetector, FrameReader) is GPLv3-compatible.
  - FastYuv and FrameReader.Native are LGPL-2.1-or-later, which can be distributed under GPLv3.
- jellyfin-ffmpeg is a GPLv3 build. Calling its executable creates no licence obligation for us; loading its libraries
  in-process happens inside a GPLv3 host anyway.
- Nothing in this repository changes licence.

**Upstream acceptance**
- Intro Skipper already covers credits and black frames with its own visual analysis, and has no request for shot
  detection.
- A contribution there would be a new analyzer behind its internal `IMediaFileAnalyzer`, which pulls ShotDetector in
  as a dependency. Its maintainers would have to want that; ask first, as the issue says.
- Chapters are outside Intro Skipper's purpose (segments), so an own plugin for chapters doesn't compete with it.
- The official Jellyfin catalogue's inclusion rules weren't found in the template or the docs, so this is unverified.
  Third-party plugin repositories (a manifest URL users add) need no approval.

**Runtime**
- Jellyfin 12 only (net10.0). Supporting 10.10/10.11 would mean building our libraries for net8.0/net9.0, which isn't
  planned.
- The plugin's load context doesn't resolve native libraries (no `LoadUnmanagedDll`) and loads every `.dll` as managed.
  The executable path avoids both problems.
- `FFmpegLibraries.Load` loads once per process (`FFmpegLibraries.cs:30`), so in-process decoding would share FFmpeg
  with anything else in the server process that loaded it first.
- AOT and trimming don't apply: Jellyfin runs plugins under the JIT, untrimmed.
- Cost: a library scan of whole films is long. `FrameSkip`/`Downscale`, sequential mode and the task's cancellation
  have to keep it bounded. Jellyfin's 4 h limit applies to the chapter-image task, not to ours.
- Exactness: with `FrameSkip` or `Downscale` set, the results aren't scenedetect's defaults. That's fine for chapters,
  but the plugin shouldn't claim parity.

## 5. Decision questions for the owner

1. Own plugin (a new GPL-3.0 repository, say `bisforboman/jellyfin-plugin-shotchapters`) or a contribution to Intro
   Skipper (ask its maintainers first), or both (chapters as our own plugin, a shot analyzer offered upstream)?
2. Jellyfin 12.x only, or also 10.10/10.11 (multi-targeting net8.0/net9.0 here)?
3. Chapters only at first, or chapters and black/fade segments together?
4. Should chapter picking and black spans become ShotDetector APIs (a release here), or stay in the plugin until
   another user wants them?
5. Should the black-frame measure (`PictureMeter`, shotdetector-web) move into ShotDetector/FrameReader, or are
   ThresholdDetector's `average_rgb` spans enough?
6. Executable only, or also in-process decoding (jellyfin-ffmpeg's libraries, or ours bundled per RID)?
