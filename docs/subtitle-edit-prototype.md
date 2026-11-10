# Subtitle Edit's shot changes: the prototype and the numbers (issue #633)

Status: **prototype done, numbers in (2026-11-09); nothing sent to Subtitle Edit.** Whether to approach Subtitle Edit
is the owner's call ([decisions/2026-11-09-subtitle-edit-prototype.md](decisions/2026-11-09-subtitle-edit-prototype.md)).
This follows the scoping note [ideas/subtitle-edit.md](ideas/subtitle-edit.md) (2026-10-31), which shipped
`--save-shotchanges` and parked the Subtitle Edit (SE) side until numbers showed a gain.

What was used:

- **SE**: `SubtitleEdit/subtitleedit` `main` at `dd83c40` (2026-11-09), the Avalonia version (SE5). The prototype is a
  local branch of a clone, and it went nowhere else.
- **ShotDetector**: the `ShotDetector` 3.30.0 package from nuget.org (with FrameReader 2.24.0) in the stand-in, and
  `shotdetect` built from `main` at `9e9ea9f` for the measurements.
- **FFmpeg**: BtbN's 8.1 build (the session's pinned one), for both SE's command and our decoding.

## In short

- **Licences: fine.** SE5 is MIT and SE4 is GPL-3.0, and every licence in our packages fits both. The one condition
  that bites is the old one: **FastYuv (LGPL-2.1) must not go into SE's single-file executable.** The prototype
  doesn't use it.
- **Integration: about 130 lines in SE, but only through the ffmpeg executable.** SE references FFmpeg.AutoGen 9 for
  its player, which overrides our `[8.1, 9)` range. Our in-process decoding then fails at once, and the stand-in
  showed it. The executable route works, but it also needs **ffprobe**, which SE's downloader doesn't fetch (SE's own
  docs list it as optional).
- **Numbers: mixed.** These are 243 hand-labelled cuts on five clip windows. At SE's default (slider 0.4), FFmpeg's
  `scene` finds **50 %** of the cuts with 7 false ones. Adaptive at the mapped threshold finds **81 %**, with 67 false
  ones.
  - On clean digital material (Sintel, Tears of Steel), adaptive is better on both counts: 13 cuts missed of 95 against
    34, and no false cuts for either.
  - On material with **repeated frames** (a telecined 1951 film, and a low-bitrate encode that drops and repeats
    frames), adaptive flags dozens of false cuts. Its ratio against neighbouring frames spikes after a repeated frame.
  - **FFmpeg at 0.2** (92 % found, 28 false) beats every ShotDetector setting tried, so a lower default slider in SE
    would win most of the gain without us.
- **Run time:** comparable. On the 12-minute film, in-process took 0.9× FFmpeg's wall time. The executable route took
  about 1.1×. Short clips pay .NET's start-up.
- **Recommendation: don't approach SE with these numbers yet.** First make adaptive robust to repeated frames (a new
  backlog row, an owner's decision because it departs from scenedetect), then measure again. Details in §5.

## 1. Licences

The issue says "Subtitle Edit is GPL-3.0". That is true of SE4 only (`se4-legacy`, `LICENSE.txt`). **SE5 (`main`) is
MIT** (`LICENSE`, "Copyright (c) 2026 Nikolaj Olsson"; checked again at `dd83c40`).

| Our part | Licence | Inside it | In SE5 (MIT) | In SE4 (GPL-3.0) |
|---|---|---|---|---|
| ShotDetector | MIT | ports of PySceneDetect (BSD-3-Clause), its simpletable (MIT) and OpenCV (Apache-2.0); notices in the package's THIRD-PARTY-NOTICES.md | Fine | Fine: MIT, BSD-3 and Apache-2.0 are all GPL-3.0-compatible (Apache-2.0 is not GPL-2.0-compatible, which doesn't matter here) |
| FrameReader | MIT | FFmpeg.AutoGen bindings (MIT, a package of its own) | Fine | Fine |
| FrameReader.Native.\<rid\> | LGPL-2.1-or-later (FFmpeg, LAME), plus BSD (dav1d, libwebp), zlib, Apache-2.0 (OpenSSL on Linux) | native shared libraries | Fine as separate, replaceable files | Fine |
| ShotDetector.FastYuv | LGPL-2.1-or-later (a swscale port) | a managed DLL | **Only outside the single file** | Fine (LGPL code may go into GPL-3.0 work) |

Conditions:

1. **Attribution.** SE ships ShotDetector's `LICENSE` and `THIRD-PARTY-NOTICES.md`: MIT, BSD-3 and Apache-2.0 all
   require the notices to travel with the binary. SE already has `installer/macBundle/THIRD_PARTY_LICENSES.txt` and
   `docs/third-party-components.md` for this.
2. **FastYuv stays out of the single file.** SE publishes with `-p:PublishSingleFile=true` (`.github/workflows/build-ui.yml`).
   That folds managed DLLs into `SubtitleEdit.exe`, where a user can't replace the LGPL part. Either leave FastYuv out
   (the prototype does; FastYuv only changes results on ARM, as README's ARM notes say) or mark it
   `ExcludeFromSingleFile`. Our package doesn't pull FastYuv in by itself.
3. **FrameReader.Native, if SE ever takes it.** SE doesn't set `IncludeNativeLibrariesForSelfExtract`, so native
   libraries already stay as separate files next to the executable, as libmpv does today. That meets the LGPL's
   replaceability. Its source offer is FrameReader's (the build scripts and FFmpeg's source are in
   `github.com/bisforboman/FrameReader`). The prototype doesn't need it: it decodes with SE's ffmpeg executable.
4. **SE's ffmpeg executable** is whatever SE downloads (often a GPL build). Running it as a separate process is mere
   aggregation, so it puts nothing on our code. SE does this already.

So the combination is fine, with conditions 1 and 2. The licence was never the obstacle.

## 2. How SE generates shot changes today

Unchanged since the scoping note, re-read at `dd83c40`:

- **Video → Shot changes → Generate** (`src/ui/Features/Video/ShotChanges/ShotChangesViewModel.cs`,
  `GenerateShotChangesFfmpeg`) runs the ffmpeg executable (`FfmpegGenerator.GetProcess`, path from
  `FfmpegHelper.GetFfmpegLocation()`) with `Se.Settings.Video.ShowChangesFFmpegArguments`, by default
  `-i "{0}" -vf "select=gt(scene\,{1}),showinfo" -threads 0 -f null -` (`src/ui/Logic/Config/SeVideo.cs:192`). `{1}`
  is the slider: 0.05 to 0.95 in 0.01 steps, default 0.4 (`ShotChangesWindow.cs`, `SeWaveform.cs:173`).
- It prepends `-progress` arguments when the duration is known (`FfmpegProgressTracker`). It parses `pts_time:` from
  showinfo's lines (`TimeRegex`) and keeps times **above 0.2 s**. When ffmpeg exits with any found, it closes with OK.
- The same command runs silently when a video opens if `ShotChangesAutoGenerate` is on (off by default), at
  below-normal priority (`MainViewModel.ExtractShotChanges`). It publishes cuts to the waveform as they arrive and saves
  them as `.shotchanges` (`ShotChangesHelper.SaveShotChanges`).

## 3. The prototype

### How it works

A setting picks the engine, and both entry points branch on it:

- `SeVideo.ShotChangesEngine`: `"FFmpeg"` (default) or `"ShotDetector"`. There is no UI for it in the prototype; it's
  set in SE's settings file. A real change would put an engine choice in the Generate tab.
- `src/ui/Logic/Media/ShotDetectorShotChanges.cs` (new) calls
  `ShotDetection.DetectAsync(video, new DetectionOptions { Detector = Adaptive, Threshold = slider × 7.5, Decoder =
  FfmpegProcess, FfmpegDirectory = <SE's ffmpeg folder>, Progress = ... })`, then keeps cut times above 0.2 s, as the
  ffmpeg route does.
- **The slider mapping**: adaptive threshold = slider × 7.5, linear. That puts SE's default 0.4 on adaptive's default
  3, and higher means fewer cuts in both, as SE's label implies. The range 0.05-0.95 becomes 0.375-7.125. Adaptive's
  other floor, `min_content_val` 15, still holds at the low end. This is a guess at a mapping, not a fitted one, and
  SE's maintainer would have the say.
- The Generate dialog runs it on a background task. It drives the progress bar from `DetectionProgress.Fraction`,
  cancels through a `CancellationToken` (Cancel), and fills the same list and closes with OK.
- Auto-generate on open runs it too. It publishes the cuts once at the end, and saves the `.shotchanges` file as
  before.

### Why the ffmpeg executable, and what that costs

SE's `UI.csproj` references **FFmpeg.AutoGen 9.0.1.1** for its own player. ShotDetector and FrameReader declare
`[8.1.0, 9.0.0)`. NuGet resolves 9.0.1.1 with warning NU1608, and FrameReader's 8.x calls then run against 9's
bindings. In the stand-in (below), `Decoder = InProcess` with our FFmpeg 8.1 libraries failed at once:

```
ShotDetectionException: In-process decoding needs FFmpeg 8's shared libraries (libavcodec 62, ...) ...
(Specified method is not supported.)
```

The same code with FFmpeg.AutoGen 8.1.0 decoded in-process and found the same 7 cuts. So inside SE, ShotDetector must
use `VideoDecoder.FfmpegProcess`, which runs **ffmpeg and ffprobe**. With a folder holding only `ffmpeg`, it stops with
`Could not start .../ffprobe`. SE's downloader fetches ffmpeg only (`DownloadHashManager.cs`), and its
`docs/third-party-components.md` lists `ffprobe.exe` as optional. A real integration needs one of:

1. SE downloads ffprobe as well (it is in the same FFmpeg builds).
2. SE runs our `shotdetect` release binary as an external engine, as it runs Whisper. That needs no ffprobe and no
   FFmpeg.AutoGen, but has the gaps the scoping note lists (`win-arm64`, `osx-x64`, machine-readable progress).
3. We move FrameReader to FFmpeg 9, which a Waiting backlog row ties to opencv-python's wheels.

### What was checked, and what wasn't

- **Checked:** the SE patch's new method was compiled and run in a stand-in console app (net10.0) with SE's package
  set for this purpose: FFmpeg.AutoGen 9.0.1.1 and ShotDetector 3.30.0. That run produced the in-process failure, the
  executable route's result and the ffprobe failure above. Compiling the stand-in also caught one bug in the first
  draft (`DetectionProgress.Fraction` is nullable).
- **Not checked:** SE itself was **not built or run** in this session. The session's sandbox refused to run an outside
  repository's build (MSBuild runs that repository's own targets). The ViewModel and MainViewModel changes are written
  against SE's code as read, and the Avalonia UI was not exercised. Building and clicking through it is the next step,
  if the work goes on, and needs SE's build allowed or a machine of the owner's.

### The patch

Against `SubtitleEdit/subtitleedit` `dd83c40`; also in
[subtitle-edit-prototype/subtitle-edit.diff](subtitle-edit-prototype/subtitle-edit.diff). Local only: no fork, no
push.

```diff
diff --git a/src/ui/Features/Main/MainViewModel.cs b/src/ui/Features/Main/MainViewModel.cs
index c10ce86..e0b6ccd 100644
--- a/src/ui/Features/Main/MainViewModel.cs
+++ b/src/ui/Features/Main/MainViewModel.cs
@@ -31962,6 +31962,27 @@ public partial class MainViewModel :
 
         WaveformGeneratingText = Se.Language.Main.ExtractingShotChanges;
 
+        if (ShotDetectorShotChanges.IsSelected)
+        {
+            // Published once, when the whole file is done (the ffmpeg route below publishes as cuts arrive).
+            // Prototype: ShotDetector's ffmpeg processes run at normal priority, not below normal.
+            var token = _videoOpenTokenSource.Token;
+            _ = Task.Run(async () =>
+            {
+                try
+                {
+                    var cuts = await ShotDetectorShotChanges.DetectAsync(videoFileName, Se.Settings.Waveform.ShotChangesSensitivity, null, token);
+                    PublishShotChanges(cuts, 0);
+                    ShotChangesHelper.SaveShotChanges(videoFileName, cuts, audioTrackNumber);
+                }
+                catch
+                {
+                    // As the ffmpeg route: a cancelled or failed silent run leaves the shot changes as they were.
+                }
+            });
+            return;
+        }
+
         var threshold = Math.Round(Se.Settings.Waveform.ShotChangesSensitivity, 2).ToString(CultureInfo.InvariantCulture);
         var argumentsFormat = Se.Settings.Video.ShowChangesFFmpegArguments;
         var arguments = string.Format(argumentsFormat, videoFileName, threshold);
diff --git a/src/ui/Features/Video/ShotChanges/ShotChangesViewModel.cs b/src/ui/Features/Video/ShotChanges/ShotChangesViewModel.cs
index 7a4d7e9..dfd330d 100644
--- a/src/ui/Features/Video/ShotChanges/ShotChangesViewModel.cs
+++ b/src/ui/Features/Video/ShotChanges/ShotChangesViewModel.cs
@@ -51,6 +51,7 @@ public partial class ShotChangesViewModel : ObservableObject, IClosingCleanup
 
     private string _videoFileName = string.Empty;
     private Process? _ffmpegProcess;
+    private CancellationTokenSource? _shotDetectorCancel;
     private FfmpegProgressTracker? _progressTracker;
     private readonly System.Timers.Timer _timerGenerate;
     private bool _doAbort;
@@ -158,6 +159,12 @@ public partial class ShotChangesViewModel : ObservableObject, IClosingCleanup
 
         IsGenerating = true;
 
+        if (ShotDetectorShotChanges.IsSelected)
+        {
+            GenerateShotChangesShotDetector();
+            return;
+        }
+
         // Tick snapping leaves float noise (0.30000000000000004); ffmpeg gets the two decimals the label shows.
         var threshold = Math.Round(Sensitivity, 2).ToString(CultureInfo.InvariantCulture);
         var argumentsFormat = Se.Settings.Video.ShowChangesFFmpegArguments;
@@ -185,6 +192,62 @@ public partial class ShotChangesViewModel : ObservableObject, IClosingCleanup
         _timerGenerate.Start();
     }
 
+    private void GenerateShotChangesShotDetector()
+    {
+        ProgressValue = 0;
+        ProgressText = Se.Language.General.StartingDotDotDot;
+        var cancel = _shotDetectorCancel = new CancellationTokenSource();
+        var progress = new Progress<double>(percent => Dispatcher.UIThread.Post(() =>
+        {
+            if (IsGenerating)
+            {
+                ProgressValue = percent;
+                ProgressText = $"{percent:0}%";
+            }
+        }));
+
+        _ = Task.Run(async () =>
+        {
+            List<double> cuts;
+            try
+            {
+                cuts = await ShotDetectorShotChanges.DetectAsync(_videoFileName, Sensitivity, progress, cancel.Token);
+            }
+            catch (Exception exception)
+            {
+                if (exception is not OperationCanceledException)
+                {
+                    Log.AppendLine(exception.Message);
+                }
+
+                Dispatcher.UIThread.Post(() =>
+                {
+                    _doAbort = false; // Cancel's flag is for the ffmpeg route's timer, which isn't running
+                    IsGenerating = false;
+                    ProgressValue = 0;
+                    ProgressText = exception is OperationCanceledException ? string.Empty : exception.Message;
+                });
+                return;
+            }
+
+            Dispatcher.UIThread.Invoke(() =>
+            {
+                foreach (var seconds in cuts)
+                {
+                    FfmpegLines.Add(new TimeItem(seconds, FfmpegLines.Count));
+                }
+
+                LastSeconds = cuts.Count > 0 ? cuts[^1] : 0;
+                IsGenerating = false;
+            });
+
+            if (cuts.Count > 0)
+            {
+                Ok();
+            }
+        });
+    }
+
     private void OutputHandler(object sendingProcess, DataReceivedEventArgs outLine)
     {
         if (string.IsNullOrWhiteSpace(outLine.Data))
@@ -311,6 +374,7 @@ public partial class ShotChangesViewModel : ObservableObject, IClosingCleanup
         if (IsGenerating)
         {
             _doAbort = true;
+            _shotDetectorCancel?.Cancel();
             return;
         }
 
diff --git a/src/ui/Logic/Config/SeVideo.cs b/src/ui/Logic/Config/SeVideo.cs
index 23919b9..4960672 100644
--- a/src/ui/Logic/Config/SeVideo.cs
+++ b/src/ui/Logic/Config/SeVideo.cs
@@ -40,6 +40,8 @@ public class SeVideo
     public List<string> RecentFiles { get; set; } = new();
     public string CutType { get; set; }
     public string ShowChangesFFmpegArguments { get; set; }
+    /// <summary>"FFmpeg" (the scene filter) or "ShotDetector" (PySceneDetect's adaptive detector).</summary>
+    public string ShotChangesEngine { get; set; } = "FFmpeg";
     public bool VideoPlayerDisplayTimeLeft { get; set; }
     public string CutDefaultVideoExtension { get; set; }
     public bool CutAlsoCutSubtitle { get; set; }
diff --git a/src/ui/Logic/Media/ShotDetectorShotChanges.cs b/src/ui/Logic/Media/ShotDetectorShotChanges.cs
new file mode 100644
index 0000000..ec395f8
--- /dev/null
+++ b/src/ui/Logic/Media/ShotDetectorShotChanges.cs
@@ -0,0 +1,43 @@
+using Nikse.SubtitleEdit.Logic.Config;
+using ShotDetector;
+using System;
+using System.Collections.Generic;
+using System.IO;
+using System.Linq;
+using System.Threading;
+using System.Threading.Tasks;
+
+namespace Nikse.SubtitleEdit.Logic.Media;
+
+/// <summary>
+/// Shot changes from ShotDetector (a C# port of PySceneDetect, same results): its adaptive detector, which compares
+/// each frame's change with its neighbours' and so ignores camera moves and flashes that FFmpeg's scene score flags.
+/// </summary>
+public static class ShotDetectorShotChanges
+{
+    public static bool IsSelected => Se.Settings.Video.ShotChangesEngine == "ShotDetector";
+
+    /// <summary>
+    /// The slider's 0.05-0.95 onto adaptive's threshold, linearly: SE's default 0.4 is adaptive's default 3, and higher
+    /// means fewer cuts in both. Adaptive's other floor (content_val 15) still applies at the low end.
+    /// </summary>
+    public static double AdaptiveThreshold(double sensitivity) => Math.Round(sensitivity, 2) * 7.5;
+
+    public static async Task<List<double>> DetectAsync(string videoFileName, double sensitivity, IProgress<double>? progress, CancellationToken cancellationToken)
+    {
+        var ffmpeg = FfmpegHelper.GetFfmpegLocation();
+        var result = await ShotDetection.DetectAsync(videoFileName, new DetectionOptions
+        {
+            Detector = DetectorKind.Adaptive,
+            Threshold = AdaptiveThreshold(sensitivity),
+            // SE loads FFmpeg 9 through FFmpeg.AutoGen 9; ShotDetector's in-process decoding is FFmpeg 8's, so it
+            // runs the ffmpeg executable (and ffprobe, which must be next to it or on PATH).
+            Decoder = VideoDecoder.FfmpegProcess,
+            FfmpegDirectory = Path.IsPathRooted(ffmpeg) ? Path.GetDirectoryName(ffmpeg) : null,
+            Progress = progress == null ? null : new Progress<DetectionProgress>(p => { if (p.Fraction is double f) { progress.Report(f * 100); } }),
+        }, cancellationToken).ConfigureAwait(false);
+
+        // As the ffmpeg route: nothing at 0.2 s or before.
+        return result.Cuts.Select(c => c.Seconds).Where(s => s > 0.2).ToList();
+    }
+}
diff --git a/src/ui/UI.csproj b/src/ui/UI.csproj
index da8dfa2..8c2d232 100644
--- a/src/ui/UI.csproj
+++ b/src/ui/UI.csproj
@@ -32,6 +32,8 @@
 		</PackageReference>
 		<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
 		<PackageReference Include="FFmpeg.AutoGen" Version="9.0.1.1" />
+		<!-- Shot changes (prototype). Its in-process decoding needs FFmpeg.AutoGen 8 (NU1608 here), so it decodes with the ffmpeg executable. -->
+		<PackageReference Include="ShotDetector" Version="3.30.0" />
 		<PackageReference Include="Google.Cloud.TextToSpeech.V1" Version="3.18.0" />
 		<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="11.0.0-rc.3.26522.148" />
 		<PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="11.0.0-rc.3.26522.148" />
```

## 4. The comparison

### Method

**Clips** (all openly licensed, so the labels can be shared and re-checked):

| Window | File | Why |
|---|---|---|
| Sintel trailer, whole (0:52) | `sintel_trailer_1920x1080_h264.mp4` (tools/realworld.py) | Animated action, fast cutting, 1080p |
| Tears of Steel 0:00-4:00 | `tears_of_steel_1280x534_h264.mov` (tools/realworld.py) | Live-action dialogue (the bridge, the lab) |
| Tears of Steel 7:00-9:48 | the same file, up to the end credits | Live-action battle: muzzle flashes, explosions, whip pans |
| Elephants Dream 0:00-5:30 | `elephants_dream_426x240_h264.mp4` (tools/realworld.py) | Animated, dark scenes with flickering light; archive.org's 512 kb/s encode, which repeats frames |
| Duck and Cover (1951), whole (9:15) | archive.org `DuckandC1951`, `DuckandC1951_512kb.mp4` (public domain), SHA-256 `ab0a244f9705792c8b24e9ab1e1f85a7d8ea0c3c1b8cbf8810271e608b6578b6` | Documentary: 24 fps film telecined to 29.97 (repeated and blended frames), grain, white "atomic flash" frames |

**Engines**: SE's exact default command and parsing (the 0.2 s filter included) at slider 0.4 and 0.2. Against it,
`shotdetect -d adaptive -t <slider × 7.5> --save-shotchanges`, decoded in-process, with the same 0.2 s filter. The
results are the same with `--decoder process`, as they must be. ShotDetector's content detector at `-t 20` joined the
pool as a third opinion.

**Ground truth, by hand, pooled and blind**: every cut that any of the five configurations reported went into a pool
(0.3 and 0.1 were run but not labelled). Reports within 3 frames of each other were merged into one candidate, 439 in
all. Each candidate was shown as a strip of five frames (n-2, n-1 | n, n+1, n+2), 12 to a sheet, labelled only with an
id. Which engine had reported it was kept off the sheets. Each was labelled by eye (by Claude, looking at the strips):

- **C**: a hard cut, including a cut through one black, white or telecine-blended frame. An engine must find these.
- **N**: no shot change (motion, a camera move, a flash, flicker, an exposure change, an animated title). An engine
  must not flag these.
- **G**: a gradual transition (a fade or dissolve). Neutral: it counts neither way.
- **X**: Tears of Steel's end credits, outside the window.

A cut counts as found when the engine's report fell in that candidate. Limits:

- A cut that none of the five configurations reported isn't in the pool, so recall is measured against the pool, not
  against every cut. Five configurations, two of them sensitive, make that a small gap.
- About ten candidates were judgment calls (a jump cut in a telecined shot, fast motion in Tears of Steel's robot
  fight, a white flash that leads into a new shot). The labels are in
  [subtitle-edit-prototype/labels.txt](subtitle-edit-prototype/labels.txt) for anyone to dispute.

### Results

Missed cuts / falsely flagged non-cuts, per window:

| Window | Cuts | Gradual | FFmpeg `scene` 0.4 (SE's default) | ShotDetector adaptive 3 (slider 0.4) | FFmpeg `scene` 0.2 | ShotDetector adaptive 1.5 (slider 0.2) | ShotDetector content 20 |
|---|---:|---:|---:|---:|---:|---:|---:|
| Sintel trailer | 8 | 1 | 5 / 0 | 1 / 0 | 2 / 0 | 0 / 3 | 5 / 4 |
| Tears of Steel, dialogue | 44 | 0 | 14 / 0 | 1 / 0 | 0 / 0 | 0 / 4 | 2 / 0 |
| Tears of Steel, battle | 43 | 0 | 15 / 0 | 11 / 0 | 4 / 2 | 1 / 1 | 4 / 7 |
| Elephants Dream | 59 | 5 | 41 / 1 | 8 / 31 | 12 / 5 | 6 / 68 | 11 / 45 |
| Duck and Cover | 89 | 5 | 46 / 6 | 25 / 36 | 2 / 21 | 10 / 42 | 33 / 21 |
| **All** | **243** | **11** | **121 / 7** | **46 / 67** | **20 / 28** | **17 / 118** | **55 / 77** |

| Configuration | Cuts found (recall) | Of its reports, real cuts (precision) |
|---|---:|---:|
| FFmpeg `scene` 0.4 (SE's default) | 50 % | 95 % |
| ShotDetector adaptive 3 (slider 0.4) | 81 % | 75 % |
| FFmpeg `scene` 0.2 | 92 % | 89 % |
| ShotDetector adaptive 1.5 (slider 0.2) | 93 % | 66 % |
| ShotDetector content 20 | 77 % | 71 % |

Split by material, at SE's default slider:

| Material | Cuts | FFmpeg 0.4: missed / false | Adaptive 3: missed / false |
|---|---:|---:|---:|
| Clean digital (Sintel, Tears of Steel) | 95 | 34 / 0 | **13 / 0** |
| Repeated frames (Elephants Dream's encode, Duck and Cover's telecine) | 148 | 87 / 7 | 33 / **67** |

**Flashes.** The pool held 25 candidates that are flashes in the strict sense: Duck and Cover's white bomb flashes and
their ends, plus Tears of Steel's muzzle flashes, explosions and hologram flicker. At the default slider, FFmpeg flagged
6 of them and adaptive 11. Adaptive has no flash filter of its own; that is content's `min_scene_len`. Its rolling
average doesn't absorb a white-out several frames long either. **The issue's "with the flash filter" overstates what
adaptive does.**

**Why adaptive fails on repeated frames.** Adaptive divides each frame's content change by the mean change of the
frames around it. A repeated frame changes by zero. In a moving shot, the frames next to a repeat therefore get a low
neighbour mean, and the ratio jumps past 3. mpdecimate keeps 1279 of 1798 frames in Duck and Cover's first minute
(3:2 pulldown plus still frames) and 1376 of 1440 in Elephants Dream's. The false cuts sit in exactly those stretches:
Elephants Dream's slow crane shot (16 false cuts in one shot) and Duck and Cover's moving street and playground shots.
The same telecine blends smear real cuts over two frames, which is most of adaptive's 25 misses there.

scenedetect 0.7.2 behaves the same way, since our results are identical to it. This isn't a porting bug. But it
matters for subtitlers, whose sources include broadcast and film transfers.

### Run time

This is a snapshot (2026-11-09, a 4-core cloud container, three interleaved runs per engine, medians), not a benchmark
(those are the [weekly runs](https://github.com/bisforboman/shotdetector/actions/workflows/benchmark.yml)). Each figure
is ShotDetector's wall time as a multiple of SE's ffmpeg command on the same file:

| File | In-process | ffmpeg executable (what SE could use today) |
|---|---:|---:|
| Tears of Steel, 12:14, 1280×534 | 0.9× | 1.1× |
| Duck and Cover, 9:15, 320×240 | 1.6× | 2.3× |
| Elephants Dream, 10:54, 426×240 | 1.3× | 1.9× |
| Sintel trailer, 0:52, 1080p | 1.1× | 1.6× |

On the long film, the two are even. The small files take only seconds either way, so .NET's start-up and probing
weigh more there. The executable route spends 30-55 % more CPU than in-process, mostly on piping raw frames from the
second process. For a subtitler, a few seconds more on a feature film is not the deciding factor; the cut quality
is.

## 5. Recommendation

1. **Don't approach SE yet.** Honest numbers would show adaptive winning clearly on clean digital material at SE's
   default, and losing on telecined or frame-repeating sources, which subtitlers do get. They would also show that
   SE's own `scene` at 0.2 beats both. A maintainer would rightly answer "lower our default" and keep FFmpeg, with no
   new dependency and no ffprobe download.
2. **What would change that: adaptive that ignores repeated frames.** Skip frames identical (or nearly identical) to
   the previous one when computing adaptive's neighbour mean, then measure again on these labels. It departs from
   scenedetect, so it would be an option, off by default, documented with the other places we differ. That needs the
   owner's decision before any work. A new backlog row (docs/backlog.md) records it. If it brings adaptive near FFmpeg
   0.2's precision while keeping its recall, the case for SE is strong.
3. **The integration route, when it comes.** Prefer SE running our release binary over the NuGet package, until
   FrameReader moves to FFmpeg 9. The binary has no FFmpeg.AutoGen clash, no ffprobe download, and no FastYuv
   single-file question. Its gaps are in the scoping note (`win-arm64`, `osx-x64`, progress for SE).
4. **Nothing in SE without the owner's OK**: no issue, discussion, PR or fork. The question is put to the owner.

## Reproducing

Everything is in [subtitle-edit-prototype/](subtitle-edit-prototype/), run from the repository root with `shotdetect`
built in Release, BtbN's FFmpeg 8.1 on PATH and `SHOTDETECTOR_FFMPEG_LIBS` set (as `.claude/hooks/session-start.sh`
leaves a cloud session):

1. `python tools/realworld.py tears_of_steel_1280x534_h264.mov elephants_dream_426x240_h264.mp4 sintel_trailer_1920x1080_h264.mp4 --download`,
   plus Duck and Cover from archive.org (SHA-256 above) into `samples/se/`.
2. `run.py <out> <clips...>`: SE's command and `shotdetect` at sliders 0.4, 0.3, 0.2 and 0.1 → `<out>/results.json`.
3. `sheets.py <dir>` (with `out/results.json` in it): the pooled candidates, the contact sheets and `candidates.json`.
4. Label the sheets into `labels.txt` (the one here is this run's), then `score.py <dir>` prints the tables above.
5. `timing.py <rounds> <scratch file> <clips...>`: the run times.

The Duck and Cover file isn't added to tools/realworld.py, which would put it in the nightly comparison. Doing so is
easy (the SHA-256 is above) if the owner wants a telecined film there.
