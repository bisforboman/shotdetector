# Mutations for tools/mutation/Invoke-Mutations.ps1: each breaks one guard on purpose (Find must occur exactly once
# in File; Replace takes its place), and at least one test in Tests (a test class filter) must then fail. A mutation
# that survives is a guard no test covers. Add one for every new guard that reproduces a scenedetect quirk.
# Project: the test project the Tests filter runs in (default tests/ShotDetector.Tests).
@{
    Mutations = @(
        # Time range, frame skip and crop (scenedetect's time, --frame-skip, --crop)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'oneBasedFrames: true'; Replace = 'oneBasedFrames: false'; Tests = 'RangeTests' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'endFrame is { } e && frameCount % stride == 0 && '; Replace = 'endFrame is { } e && '; Tests = 'RangeTests' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (o.FrameSkip > 0 && o.CollectStats)'; Replace = 'if (false)'; Tests = 'RangeTests' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'effective = (crop.Width + 1, crop.Height + 1);'; Replace = 'effective = (crop.Width, crop.Height);'; Tests = 'RangeTests' }

        # A container's clean aperture (MOV/MP4 'clap') is ignored, as OpenCV ignores it: ffmpeg's command line crops to it
        # from FFmpeg 7.1 unless told -apply_cropping codec (detection and save-images)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '.. IgnoreContainerCropping(FfmpegExe, cancellationToken), "-noautorotate"'; Replace = '"-noautorotate"'; Tests = 'CleanApertureTests' }
        @{ File = 'src/ShotDetector/Export.cs'; Find = '.. VideoReader.IgnoreContainerCropping(video.FfmpegExe, cancellationToken), "-i"'; Replace = '"-i"'; Tests = 'CleanApertureTests' }

        # 10-bit: chroma interpolated from where the frame says it sits, as OpenCV's sws_scale_frame does (executable
        # path: in_chroma_loc; in-process sampled rows: the position sws_scale_frame derives)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '(siting is { } cl ? ":in_chroma_loc=" + cl : "")'; Replace = '""'; Tests = 'InProcessTests.SameAsTheFfmpegExecutableForEachFormat' }

        # ... each frame's, where the siting changes midway: the executable reads a file again from the change with the
        # new in_chroma_loc (its scale filter takes one), dropping the earlier encode's frames that share the later one's
        # first times, counting its own frames and keeping the frame labels found so far; a stream says it couldn't
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'bool followSiting = !yuv && _pixelFormat is not ("yuv420p" or "yuvj420p");'; Replace = 'bool followSiting = false;'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'string? siting = restart is null ? ChromaLocation : restart.Location;'; Replace = 'string? siting = ChromaLocation;'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'bool waitingForSiting = restart is not null, stopped = false;'; Replace = 'bool waitingForSiting = false, stopped = false;'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'string[] limit = count is { } n && restart is null ?'; Replace = 'string[] limit = count is { } n ?'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (!Streaming && restart is null)'; Replace = 'if (!Streaming)'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'SitingChangeNotFollowed = true;'; Replace = ';'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }

        # Reference tone on frames from the caller (#629): trimmed by YuvVideo.ReferenceTone as the file path trims by the
        # tone it finds; refused without it, and for ranges (a time range)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'tone = !video.FromFrames'; Replace = 'tone = video.FromFrames'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'video.GivenTone ?? throw'; Replace = 'video.GivenTone ?? Array.Empty<FrameReader.ToneSegment>() ?? throw'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        # ... for ranges with the caller's tone: the frames before the run's start score nothing, its first is the
        # detectors' first; Merge decides from there to where the end stops the run, takes those frames' stats, replays
        # freezedetect from there; the tone is in the fingerprint
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (o.TrimReferenceTone && video.ReferenceTone is null)'; Replace = 'if (false)'; Tests = 'RangeToneTests.TheToneIsInTheFingerprint' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'int from = Math.Max(lead, start);'; Replace = 'int from = lead;'; Tests = 'RangeToneTests.AnySplitIsDetects' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (firstFrame <= toneStart)'; Replace = 'if (false)'; Tests = 'RangeToneTests.AnySplitIsDetects' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'var joined = new BlackFrames(reader.BlackTimeBase, start);'; Replace = 'var joined = new BlackFrames(reader.BlackTimeBase, 0);'; Tests = 'RangeToneTests.AnySplitIsDetects' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (_reader is null && _metrics[0].Count <= _start)'; Replace = 'if (false)'; Tests = 'RangeToneTests.OnShotTellsTheRunsShots' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'Line(nameof(o.TrimReferenceTone), string.Join('; Replace = '_ = (nameof(o.TrimReferenceTone), string.Join('; Tests = 'RangeToneTests.TheToneIsInTheFingerprint' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (endFrame is { } e && video.Position(k).PlusFrames(1).FrameNum >= e)'; Replace = 'if (false)'; Tests = 'RangeToneTests.AnySplitIsDetects' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = '(int)Math.Min(chunkStats.To, (long)startFrame + frameCount)'; Replace = 'chunkStats.To'; Tests = 'RangeToneTests.AnySplitIsDetects' }

        # Timestamps that start over (two encodes joined): known before the first frame (a file's packets, MPEG-PS's
        # pass; MPEG-PS's arriving ones may), the shots are told at the end, as the sorted cuts make them
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = ' && !video.TimestampsStartOver;'; Replace = ';'; Tests = 'TimestampResetTests' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '_startsOver = probed?.StartsOver ?? (!missingPts && StartsOver(pts));'; Replace = '_startsOver = false;'; Tests = 'TimestampResetTests.AFileTellsTheResultsShots' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'internal bool TimestampsStartOver => _ptsPending || _startsOver;'; Replace = 'internal bool TimestampsStartOver => _startsOver;'; Tests = 'TimestampResetTests.MpegPsWithArrivingTimestampsTellsTheShotsAtTheEnd' }

        # The first frame's chroma location and colour tags over the stream's (the container's header can contradict the
        # codec's, which OpenCV converts with): executable path's in_chroma_loc, FastYuv's colours and siting
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (key is "chroma_location" or "color_space" or "color_range" && value is not (null or "unspecified" or "unknown" or ""))'; Replace = 'if (false)'; Tests = 'VideoTests.TheFramesChromaLocationAndColourTagsWinOverTheStreams' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'PreferFrameTags(stream, frameTags.Value);'; Replace = '_ = frameTags;'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }

        # ... and in-process, FastYuv converts each frame with its own (a conversion per set of colours), as OpenCV does
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'if (tags == _lastTags)'; Replace = 'if (_lastTags is not null)'; Tests = 'FastYuvTests.InProcessFollowsEachFramesTags' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'PreferFrameTag(stream, "color_space", colorSpace);'; Replace = ''; Tests = 'FastYuvTests.InProcessFollowsEachFramesTags' }

        # ... and through the executable, from showinfo: the siting on the frame's line, the matrix and range on the line
        # after it (a tag cut short by another thread's output is the previous frame's); colours FastYuv declines send the
        # video to the BGR pipelines
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'livePts.Add(waiting with { Tags = previous = tags });'; Replace = 'livePts.Add(waiting);'; Tests = 'FastYuvTests.ExecutableFollowsEachFramesTags' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'previous = previous with { ChromaLocation = SitingField(line) ?? previous.ChromaLocation },'; Replace = 'previous = previous with { ChromaLocation = null },'; Tests = 'FastYuvTests.ExecutableFollowsEachFramesTags' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'ColorSpace = ColourSpaces.Contains(space) ? space : frame.ColorSpace,'; Replace = 'ColorSpace = space,'; Tests = 'FastYuvTests.ShowinfoTagsCutShortAreThePreviousFramesTags' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'GeneratedRegex(@" cl:([a-z]+) ")]'; Replace = 'GeneratedRegex(@" cl:([a-z]+)")]'; Tests = 'FastYuvTests.ShowinfoTagsCutShortAreThePreviousFramesTags' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'conversion?.Yuv ?? YuvForVideo!'; Replace = 'YuvForVideo!'; Tests = 'FastYuvTests.ExecutableFollowsEachFramesTags' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (yuv && conversion is null && _stream is null && !Streaming)'; Replace = 'if (false)'; Tests = 'FastYuvTests.ExecutableTakesTheDefaultPathForColoursFastYuvDeclines' }

        # ... read only where FastYuv or the executable's scale filter uses them (not the in-process default path), once
        # for a detection's chunks
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'YuvForVideo => _yuv420 is null ? null : _colours.Value.YuvForVideo;'; Replace = 'YuvForVideo => _colours.Value.YuvForVideo;'; Tests = 'InProcessTests.ReadsTheFirstFramesTagsOnlyWhereTheyreUsed' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '_frameTags = probed is not null ? probed.FrameTags'; Replace = '_frameTags = false ? probed!.FrameTags'; Tests = 'InProcessTests.ReadsTheFirstFramesTagsOnlyWhereTheyreUsed' }

        # The executable's raw-YUV pipe labels every frame with the first frame's colour tags, so a matrix or range that
        # changes midway doesn't make ffmpeg convert the later frames' samples (FastYuv reads the decoder's)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'select[1] + "," + _colours.Value.YuvLabels'; Replace = 'select[1]'; Tests = 'FastYuvTests.ExecutableFollowsEachFramesTags' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'colorSpace is { } cs && SetParamsColorSpaces.Contains(cs) ? cs : "unknown"'; Replace = 'colorSpace ?? "unknown"'; Tests = 'UrlOptionTests.TheYuvPipesLabelsAreTheFirstFramesTags' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'pixelFormat.StartsWith("yuvj", StringComparison.Ordinal) ? "pc"'; Replace = 'false ? "pc"'; Tests = 'UrlOptionTests.TheYuvPipesLabelsAreTheFirstFramesTags' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'colorRange is "tv" or "pc" ? colorRange : "unknown"'; Replace = 'colorRange ?? "unknown"'; Tests = 'UrlOptionTests.TheYuvPipesLabelsAreTheFirstFramesTags' }

        # compare.py on Windows: BT.601 whatever the colour tags, as the Windows OpenCV wheel (executable, FastYuv, not in-process)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '+ (_alwaysBt601 ? ":in_color_matrix=bt601" : "")'; Replace = '+ ""'; Tests = 'VideoTests.AlwaysBt601IgnoresTheColourTags' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '_alwaysBt601 ? "bt470bg" : '; Replace = ''; Tests = 'VideoTests.AlwaysBt601IgnoresTheColourTags' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '&& !_inProcessGaveNothing && !_alwaysBt601'; Replace = '&& !_inProcessGaveNothing'; Tests = 'VideoTests.AlwaysBt601IgnoresTheColourTags' }

        # scenedetect 0.7.2: min_scene_len as a frame count (#577), adaptive from the target frame (#408), min_out_length, defaults
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = '? position(frame).Minus(position(since)).FrameNum >= minSceneLen'; Replace = '? position(frame).Minus(position(since)).Seconds >= _minSeconds'; Tests = 'DetectorTests.ContentMinLengthInFramesComparesFrameCountsFrom072' }
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = 'position(fromTarget ? targetFrame : frame)'; Replace = 'position(frame)'; Tests = 'DetectorTests.AdaptiveMinLengthIsMeasuredFromTheTargetFrameFrom072' }
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = '&& position(frame).Minus(position(_lastFadeFrame)).FrameNum >= minOutLength'; Replace = ''; Tests = 'ThresholdDetectorTests.FadeOutsShorterThanMinOutLengthMakeNoCutFrom072' }
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = '&& end.PlusFrames(1).Minus(position(_lastFadeFrame)).FrameNum >= minOutLength'; Replace = ''; Tests = 'ThresholdDetectorTests.FadeOutsShorterThanMinOutLengthMakeNoCutFrom072' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = '(value ?? (v072 ? "0.1s" : null))'; Replace = '(value)'; Tests = 'ThresholdDetectorTests.MinOutLengthDefaultsToTheClisFrom072' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'v072 ? 128 : 256'; Replace = '256'; Tests = 'VideoTests.HistogramAndHashDefaultsFollowTheCompatibilityRelease' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'v072 ? 8 : 16'; Replace = '16'; Tests = 'VideoTests.HistogramAndHashDefaultsFollowTheCompatibilityRelease' }

        # Hash detector: the same bits on every runtime (near-flat frames, whose DCT is noise around the median): the
        # defaults' cosine tables checked in (Math.Cos differs on browser-wasm), numpy's median of the two middle values
        @{ File = 'src/ShotDetector/HashDetector.cs'; Find = 'HashCosines.For(n, rows) ?? '; Replace = ''; Tests = 'HashDetectorTests' }
        @{ File = 'src/ShotDetector/HashDetector.cs'; Find = '(float)((float)(sorted[half - 1] + sorted[half]) / 2f)'; Replace = 'sorted[half]'; Tests = 'HashDetectorTests' }

        # Rotation as OpenCV's: the converted image turned by the display matrix's angle, not ffmpeg's autorotate
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'cancellationToken), "-noautorotate", "-i", _path,'; Replace = 'cancellationToken), "-i", _path,'; Tests = 'InProcessTests.RotatesAsTheFfmpegExecutable' }

        # FastYuv as x86's swscale: 4:2:2 reads its own row's chroma (YUV2RGB_LOOP's vshift), only where
        # swscale takes the unscaled converter (even decoded height), converted before turning (cv::rotate)
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.cs'; Find = 'RowToBgr(yuv, width, height, height, y, y, cols, bgrRow);'; Replace = 'RowToBgr(yuv, width, height, height, y, y >> 1, cols, bgrRow);'; Tests = 'FastYuvTests' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (!TenBit && _codedSize.H % 2 != 0)'; Replace = 'if (false)'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }

        # FastYuv for 10-bit 4:2:0 as x86's swscale scales it: any height, even widths, MMXEXT's vertical scaler (from
        # vRounder) and output except the last two rows (C, chroma clipped), chroma interpolated from where it sits
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'bool asm = y < height - 2;'; Replace = 'bool asm = true;'; Tests = 'FastYuvTests.Converts10BitAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'const int VRounder = 4;'; Replace = 'const int VRounder = 0;'; Tests = 'FastYuvTests.Converts10BitAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'Math.Clamp(uc >> 19, 0, 255)'; Replace = '((uc >> 19) & 255)'; Tests = 'FastYuvTests.Converts10BitAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'SwscaleFilter.LocalPos(1, hChrPos)'; Replace = 'SwscaleFilter.LocalPos(1, -513)'; Tests = 'FastYuvTests.Converts10BitAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'if (width % 2 != 0 || width < 2'; Replace = 'if (width < 2'; Tests = 'FastYuvTests.TenBitDeclinesWhatItDoesNotReproduce' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'h.Size <= cw && (chroma422 || v.Size > 2)'; Replace = 'h.Size <= cw'; Tests = 'FastYuvTests.TenBitDeclinesWhatItDoesNotReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '_codedSize.W, _codedSize.H, Turn, CropRegion.X'; Replace = '_codedSize.W, _codedSize.H, 0, CropRegion.X'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }

        # ... and 8-bit 4:2:0 and 4:2:2 of an odd height (#630), which swscale scales too: the 8-bit samples widened to 10
        # (times 4, not swscale's 8-to-10 expansion), chroma sited as the frame says, 4:2:2's yuv2bgr24_1 (no rounder,
        # psraw 4), only odd heights
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '=> TenBit || _codedSize.H % 2 != 0;'; Replace = '=> TenBit;'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'to[i] = (ushort)(from[i] << 2);'; Replace = 'to[i] = (ushort)((from[i] << 2) | (from[i] >> 6));'; Tests = 'FastYuvTests.ConvertsOddHeightsAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = '_rounder = plan.Chroma422 ? 0 : VRounder;'; Replace = '_rounder = VRounder;'; Tests = 'FastYuvTests.ConvertsOddHeightsAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'int uu = (short)((f[k] >> 4) - 1024)'; Replace = 'int uu = (short)((f[k] + 8 >> 4) - 1024)'; Tests = 'FastYuvTests.ConvertsOddHeightsAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.cs'; Find = 'height % 2 == 0 || pixelFormat is not'; Replace = 'pixelFormat is not'; Tests = 'FastYuvTests.OddHeightsDecline' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'video.PixelFormat == "yuv420p10le" || video.Height % 2 != 0'; Replace = 'video.PixelFormat == "yuv420p10le"'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }

        # FastYuv's 8-bit rows with Vector128: blocks of 16 pixels only where they fit the row, start at an even column (whole
        # chroma samples) and write only the columns asked for; a plan per set of columns and width; the columns checked
        # (the reads and writes aren't); the vectors used wherever Vector128 is accelerated
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.cs'; Find = 'if ((uint)x >= (uint)limit)'; Replace = 'if (false)'; Tests = 'FastYuvRowTests.RefusesAColumnOutsideTheRow' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.cs'; Find = 'if (Vectorized && bgrRow.Length >= width * 3 && DenseEnough(cols)'; Replace = 'if (Vectorized && DenseEnough(cols)'; Tests = 'FastYuvRowTests.RefusesAColumnOutsideTheRow' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.Vector.cs'; Find = 'if (sorted.Length > 0 && (sorted[0] < 0 || sorted[^1] >= width))'; Replace = 'if (false)'; Tests = 'FastYuvRowTests.RefusesAColumnOutsideTheRow' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.Vector.cs'; Find = 'internal bool Vectorized { get; set; } = Vector128.IsHardwareAccelerated;'; Replace = 'internal bool Vectorized { get; set; }'; Tests = 'FastYuvRowTests.ConvertersUseTheVectorsWhereAccelerated' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.Vector.cs'; Find = 'if (plan is null || plan.Width != width || !cols.SequenceEqual(plan.Cols))'; Replace = 'if (plan is null || plan.Width != width)'; Tests = 'FastYuvRowTests.ColumnsChangedInPlaceOrAnotherWidthGetTheirOwnPlan' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.Vector.cs'; Find = 'if (plan is null || plan.Width != width || !cols.SequenceEqual(plan.Cols))'; Replace = 'if (plan is null || !cols.SequenceEqual(plan.Cols))'; Tests = 'FastYuvRowTests.ColumnsChangedInPlaceOrAnotherWidthGetTheirOwnPlan' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.Vector.cs'; Find = 'if (x0 + BlockPixels > width || end - i < MinColumnsPerBlock)'; Replace = 'if (end - i < MinColumnsPerBlock)'; Tests = 'FastYuvRowTests.BlocksStayWithinTheRow' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.Vector.cs'; Find = 'int x0 = sorted[i] & ~1, end = i;'; Replace = 'int x0 = sorted[i], end = i;'; Tests = 'FastYuvRowTests.BlocksGiveTheTablesBytesForEveryYuv' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.Vector.cs'; Find = 'if (end - i == BlockPixels)'; Replace = 'if (end - i > 0)'; Tests = 'FastYuvRowTests.SameBytesAndUntouchedPixelsAtAnyWidth' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.Vector.cs'; Find = '.Fill(0xFF);'; Replace = '.Fill(0x7F);'; Tests = 'FastYuvRowTests.SameBytesAndUntouchedPixelsAtAnyWidth' }

        # FastYuv's 10-bit conversion with Vector128: pixel blocks of 16 from an even column over their 8 chroma samples
        # (consecutive in the row's terms), with enough columns, stored directly only where the columns ask for exactly
        # those pixels once each in order; a plan per set of columns; horizontal chroma blocks only over consecutive
        # samples and filter positions, x86's pmaddwd blocks only for 4 taps; the vectors used wherever accelerated
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = 'int x0 = x & ~1, asked = 0;'; Replace = 'int x0 = x, asked = 0;'; Tests = 'FastYuv10RowTests.SameBytesAndUntouchedPixelsAtAnyWidth' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = 'if (chromaCols[k0 + i] != c0 + i)'; Replace = 'if (false)'; Tests = 'FastYuv10RowTests.BlocksNeedTheirChromaAndEnoughColumns' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = 'if (k0 < 0 || k0 + BlockPixels / 2 > chromaCols.Length)'; Replace = 'if (k0 < 0)'; Tests = 'FastYuv10RowTests.SameBytesAndUntouchedPixelsAtAnyWidth' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = 'if (asked < SwscaleYuv420.MinColumnsPerBlock)'; Replace = 'if (asked < 1)'; Tests = 'FastYuv10RowTests.BlocksNeedTheirChromaAndEnoughColumns' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = 'isDirect = count[x0 + i] == 1 && Cols[j0 + i] == x0 + i;'; Replace = 'isDirect = Cols[j0 + i] == x0 + i;'; Tests = 'FastYuv10RowTests.SameBytesAndUntouchedPixelsAtAnyWidth' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = 'isDirect = count[x0 + i] == 1 && Cols[j0 + i] == x0 + i;'; Replace = 'isDirect = count[x0 + i] == 1;'; Tests = 'FastYuv10RowTests.SameBytesAndUntouchedPixelsAtAnyWidth' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = 'if (plan is null || !cols.SequenceEqual(plan.Cols))'; Replace = 'if (plan is null)'; Tests = 'FastYuv10RowTests.ColumnsChangedInPlaceOrAnotherWidthGetTheirOwnPlan' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = 'internal bool Vectorized { get; set; } = Vector128.IsHardwareAccelerated;'; Replace = 'internal bool Vectorized { get; set; }'; Tests = 'FastYuv10RowTests.ConvertersUseTheVectorsWhereAccelerated' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = 'while (m < HorizontalBlockSamples && chromaCols[k + m] == i + m && hPos[i + m] == hPos[i] + m)'; Replace = 'while (m < HorizontalBlockSamples && hPos[i + m] == hPos[i] + m)'; Tests = 'FastYuv10RowTests.HorizontalBlocksNeedConsecutiveSamplesAndPositions' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = 'while (m < HorizontalBlockSamples && chromaCols[k + m] == i + m && hPos[i + m] == hPos[i] + m)'; Replace = 'while (m < HorizontalBlockSamples && chromaCols[k + m] == i + m)'; Tests = 'FastYuv10RowTests.HorizontalBlocksNeedConsecutiveSamplesAndPositions' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = ': Sse2.IsSupported ? (size == 4 ? Horizontal.X86 : Horizontal.Scalar)'; Replace = ': Sse2.IsSupported ? Horizontal.X86'; Tests = 'FastYuv10RowTests.ConvertersUseTheVectorsWhereAccelerated' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.Vector.cs'; Find = ': PortableHorizontal ? Horizontal.Portable'; Replace = ': false ? Horizontal.Portable'; Tests = 'FastYuv10RowTests.ConvertersUseTheVectorsWhereAccelerated' }

        # In-process YUV pipelines take only the rows the conversion reads (FrameDecoder.CopyRows; with row-wise yadif only
        # those computed): the resize's luma rows (turned: the rows under its pixels) and the chroma rows the converter's
        # taps read (8-bit: its own row's or y / 2; 10-bit: the vertical filter's); whole planes for a converter that doesn't say
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.cs'; Find = '(chromaHeight == height ? y : y >> 1, 1);'; Replace = '(y >> 1, 1);'; Tests = 'FastYuvTests.RowsOutsideTheRowsReadChangeNothing' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'return (plan.V.Pos[y], plan.V.Size);'; Replace = 'return (plan.V.Pos[y], 1);'; Tests = 'FastYuvTests.RowsOutsideTheRowsReadChangeNothing' }
        @{ File = 'src/ShotDetector/CvResize.cs'; Find = 'TurnedPixels(width, height, turn, cropX, cropY).Select(p => p.Y)'; Replace = 'TurnedPixels(width, height, turn, cropX, cropY).Select(p => p.X)'; Tests = 'FastYuvTests.RowsOutsideTheRowsReadChangeNothing' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '=> conversion?.ChromaRows is { } chromaRows'; Replace = '=> conversion?.ChromaRows is var chromaRows'; Tests = 'FastYuvTests.ConvertersThatDoNotSayWhichRowsGetWholePlanes' }

        # Deinterlacing above 8 bits: yadif on one thread (FFmpeg 8.1's x86 yadif varies from run to run with slice threads
        # there, docs/ffmpeg-yadif-16bit-report.md; on one it equals the C code, scenedetect's copy made the same way)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '? "yadif=threads=1" : "yadif"'; Replace = '? "yadif" : "yadif"'; Tests = 'VideoTests.TheExecutablesYadifIsTheSameOnEveryRun' }

        # Chunked runs: twice the CPUs' decoder threads (at most 16) for frame-threaded decoders up to 1080p, shared out among the chunks
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'frameThreads && pixels <= MoreThreadsMaxPixels ? Math.Clamp(2 * cpus, 1, 16)'; Replace = 'frameThreads && pixels <= MoreThreadsMaxPixels ? Math.Clamp(cpus, 1, 16)'; Tests = 'ParallelTests.FrameThreadedDecodersGetTwiceTheCpus' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'frameThreads && pixels <= MoreThreadsMaxPixels ?'; Replace = 'frameThreads ?'; Tests = 'ParallelTests.FrameThreadedDecodersGetTwiceTheCpus' }
        # ... told by codec name on the executable path, whose FFmpeg libraries may not load
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'DecodesInProcess ? FrameReader.FrameDecoder.HasFrameThreads(Codec) : FrameThreadedCodecs.Contains(Codec)'; Replace = 'DecodesInProcess && FrameReader.FrameDecoder.HasFrameThreads(Codec)'; Tests = 'ParallelTests.TellsFrameThreadedDecoders' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '_codedSize.W, _codedSize.H, Turn, CropRegion.X'; Replace = '_codedSize.W, _codedSize.H, 0, CropRegion.X'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }
        # ... a frame at a time (each chroma row filtered once): taken when the converter has it, packed rows x columns,
        # the crop's rows, runs of rows in parallel for chunked outputs
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'c10 is IYuv420P10FrameConverter f ? f.FrameToBgr : null'; Replace = 'null'; Tests = 'FastYuvTests.PipelinesConvertTenBitAFrameAtATime' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'packed ? j * 3 : x * 3;'; Replace = 'packed ? 0 : x * 3;'; Tests = 'FastYuvTests.TenBitFrameAtATimeEqualsRowByRow' }
        @{ File = 'src/ShotDetector/CvResize.cs'; Find = '_frameRows = SourceRows.Select(r => r + cropY).ToArray();'; Replace = '_frameRows = SourceRows.ToArray();'; Tests = 'FastYuvTests.TenBitFrameAtATimeEqualsRowByRow' }
        @{ File = 'src/ShotDetector/CvResize.cs'; Find = 'bgr.AsSpan(first * n, (end - first) * n)'; Replace = 'bgr.AsSpan(0, (end - first) * n)'; Tests = 'FastYuvTests.TenBitFrameAtATimeEqualsRowByRow' }

        # Damaged files: each frame labelled by its own pts as OpenCV's CAP_PROP_POS_MSEC, not the best-effort timestamp
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'long label = LabelPts(decoder.FramePts, pts, _deinterlace);'; Replace = 'long label = pts;'; Tests = 'InProcessTests.LabelsEachFrameByItsOwnPtsAsOpenCv' }

        # Timestamps starting over (two encodes joined): labelled by time, cuts sorted, stats one row per number
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'seconds < pts[previous] * timeBase - tolerance)'; Replace = 'false)'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '&& !(writer.HasStreamSize && writer.PixelFormat == _pixelFormat);'; Replace = '&& false;'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (yuv && info.NewLayout && _stream is null && !Streaming)'; Replace = 'if (false)'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }

        # A frame in another pixel format than the stream's (4:2:2 and 4:2:0 joined): OpenCV converts it from its own,
        # so FastYuv leaves it to the exact conversion (in-process per frame; the executable reads again on BGR)
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '&& writer.PixelFormat == _pixelFormat);'; Replace = ');'; Tests = 'InProcessTests.PixelFormatChangeTakesTheExactConversion' }

        # Converting apart from decoding (ConvertThreads): by default for HD read sequentially (at least 1280 x 720's
        # pixels; not a parallel run's chunks), never with row-wise yadif (taking a frame would deinterlace all its rows),
        # and a taken frame's YUV rows where the decoder's CopyRows puts them
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'bool convertApart = convertThreads > 0 && !(_deinterlace && _yadif is not null);'; Replace = 'bool convertApart = convertThreads > 0;'; Tests = 'InProcessTests.ConvertThreadGivesTheSameFrames' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'int convertThreads = _convertThreads ?? DefaultConvertThreads(_codedSize.W, _codedSize.H);'; Replace = 'int convertThreads = _convertThreads ?? 0;'; Tests = 'InProcessTests.HdConvertsOnAThreadOfItsOwnByDefault' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'internal int? ConvertThreads { get; init; }'; Replace = 'internal int? ConvertThreads { get; init; } = 1;'; Tests = 'InProcessTests.SmallVideoConvertsOnTheDecodingThreadByDefault' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'internal const long ConvertThreadMinPixels = 1280L * 720;'; Replace = 'internal const long ConvertThreadMinPixels = 1280L * 721;'; Tests = 'InProcessTests.DefaultConvertThreadsFromHd' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'ConvertThreads = o.ConvertThreads ?? 0 };'; Replace = 'ConvertThreads = o.ConvertThreads };'; Tests = 'InProcessTests.ChunksConvertOnTheirDecodingThreadsByDefault' }
        @{ File = 'src/ShotDetector/FrameWriter.cs'; Find = 'src.Slice(y * s, pw).CopyTo(dst.AsSpan(o + (y * pw), pw));'; Replace = 'src.Slice(y * s, pw).CopyTo(dst.AsSpan(o, pw));'; Tests = 'InProcessTests.ConvertThreadGivesTheSameFrames' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = ' || (format.Success && format.Groups[1].Value != _pixelFormat)'; Replace = ''; Tests = 'InProcessTests.PixelFormatChangeTakesTheExactConversion' }
        @{ File = 'src/ShotDetector/Stats.cs'; Find = 'if (!byPosition.TryGetValue(at.Key, out int r))'; Replace = 'if (!byPosition.TryGetValue(at.Key, out int r) || true)'; Tests = 'ThresholdDetectorTests.StatsAtTheSamePositionMergeKeyByKey' }
        # Stats' rows in frame order only while each position comes after the one before and is another (one Key, one
        # (Seconds, FrameNum): frame numbers and timestamps below 2^53); a frame's keys kept in the order set (Entries)
        @{ File = 'src/ShotDetector/Stats.cs'; Find = 'if (!After(at, previous))'; Replace = 'if (false)'; Tests = 'ThresholdDetectorTests.StatsAtTheSamePositionMergeKeyByKey' }
        @{ File = 'src/ShotDetector/Stats.cs'; Find = 'at.TbDen >= 0 && '; Replace = ''; Tests = 'StatsTests.EqualPositionsWithDifferentSecondsShareARow' }
        @{ File = 'src/ShotDetector/Stats.cs'; Find = ' && at.TbDen <= 1L << 53'; Replace = ''; Tests = 'StatsTests.EqualPositionsWithDifferentSecondsShareARow' }
        @{ File = 'src/ShotDetector/Stats.cs'; Find = ' && at.Value is >= -(1L << 53) and <= 1L << 53'; Replace = ''; Tests = 'StatsTests.EqualPositionsWithDifferentSecondsShareARow' }
        @{ File = 'src/ShotDetector/Stats.cs'; Find = 'at.Seconds > p.Seconds || (at.Seconds == p.Seconds && at.FrameNum > p.FrameNum)'; Replace = 'at.Seconds >= p.Seconds'; Tests = 'StatsTests.ARepeatedPositionIsOneRow' }
        # WriteCsv streams (each row written before the next position is asked for) while positions come in order
        @{ File = 'src/ShotDetector/Stats.cs'; Find = 'if (merge || !InOrder(position))'; Replace = 'if (true)'; Tests = 'StatsTests.WriteCsvWritesEachRowBeforeFormattingTheNext' }
        # WritePyFloat writes .NET's own text only where PyFloat would (below 1e16, no exponent)
        @{ File = 'src/ShotDetector/Stats.cs'; Find = 'if (Math.Abs(d) < 1e16 && '; Replace = 'if ('; Tests = 'StatsTests.WritePyFloatIsPyFloat' }
        @{ File = 'src/ShotDetector/Stats.cs'; Find = " && buffer[..length].IndexOf('E') < 0)"; Replace = ')'; Tests = 'StatsTests.WritePyFloatIsPyFloat' }
        @{ File = 'src/ShotDetector/Stats.cs'; Find = 'if (Array.IndexOf(_orders[state], k) < 0)'; Replace = 'if (true)'; Tests = 'StatsTests.EntriesKeepEachFramesKeyOrder' }
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = 'average = stored;'; Replace = '{ }'; Tests = 'ThresholdDetectorTests.RepeatedPositionReusesTheStoredAverageWithStats' }
        @{ File = 'src/ShotDetector/FrameTime.cs'; Find = 'return (Value / g, TbDen / g);'; Replace = 'return (Value, TbDen);'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }

        # Parallel detection: what a chunk can't reproduce reads sequentially (a frame's position isn't the probe's for its
        # index; a chunk's overlap score or first frame size isn't the next one's)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (k >= a && reader.Position(k).Key != video.Position(k).Key)'; Replace = 'if (false)'; Tests = 'ParallelTests.FallsBackToSequential' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'for (int i = 0; i + 1 < chunks && problem is null; i++)'; Replace = 'for (int i = 0; false; i++)'; Tests = 'ParallelTests.SizeChangeFallsBack' }
        # ... the executable path's first frame size, from showinfo, for that check
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'FirstFrameSize = (int.Parse(w,'; Replace = '_ = (int.Parse(w,'; Tests = 'ParallelTests.FirstFrameSizeIsTheFirstGivenFramesOwn' }

        # Parallel detection says why it read sequentially, and shares the decoder threads out without losing one
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = '        fallback = problem;'; Replace = '        fallback = null;'; Tests = 'ParallelTests.FallsBackToSequential' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (o.Mode == DetectionMode.Parallel && notChunked is not null)'; Replace = 'if (false)'; Tests = 'ParallelTests.FallsBackToSequential' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = ' + (chunk < threads % chunks ? 1 : 0)'; Replace = ''; Tests = 'ParallelTests.DecoderThreadsSharedOut' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'Math.Max(2, 8 / chunks)'; Replace = '8 / chunks'; Tests = 'ParallelTests.FrameBuffersSharedOut' }

        # Auto chunks with half the CPUs (none below 4) and for frames up to 3840 x 2160 (docs/decisions/2026-10-18-parallel-auto-4cpus.md)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'Math.Min(4, cpus / 2)'; Replace = 'Math.Min(4, cpus)'; Tests = 'ParallelTests.AutomaticChunks' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (!forced && pixels > AutoChunksMaxPixels)'; Replace = 'if (false)'; Tests = 'ParallelTests.AutomaticChunksBySizeModeAndThreads' }
        # DetectionOptions.OnShot only listens: Auto still chunks a file for Detect (only DetectStreamAsync's channel reads sequentially)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'o.ResolvedMode(streaming) == DetectionMode.Parallel'; Replace = 'o.ResolvedMode(onShot is not null) == DetectionMode.Parallel'; Tests = 'OnShotTests.FilePathInChunksTellsTheResultsShots' }

        # Streamed input
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (o.StartTime is not null && video.Streaming)'; Replace = 'if (false)'; Tests = 'VideoTests' }

        # Packets without a pts (MPEG-PS): per-frame timestamps from a decoding pass, as OpenCV sees them
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '_ptsPending = missingPts && probed is null;'; Replace = '_ptsPending = false;'; Tests = 'VideoTests' }

        # Frame rate override: an image sequence is read at that rate
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'IsImageSequencePattern(path) && !IsUrl(path)'; Replace = 'false'; Tests = 'VideoTests.ImageSequenceIsReadAtTheFrameRate' }
        # ... and only a pattern image2 reads as one (av_filename_number_test): a '%' in a file's name (My%20Video.mp4) isn't
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'IsImageSequencePattern(path) && !IsUrl(path)'; Replace = 'path.Contains(''%'') && !IsUrl(path)'; Tests = 'VideoTests.APercentInAFileNameIsNoImageSequence' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (number)'; Replace = 'if (false)'; Tests = 'UrlOptionTests.ImageSequencePatternsAreImage2s' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'else if (path[j] != ''%'')'; Replace = 'else if (false)'; Tests = 'UrlOptionTests.ImageSequencePatternsAreImage2s' }

        # Dropped frames: each decoded frame takes its own packet's time (OpenCV labels frames by time)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'k < _livePackets.Count ? _livePackets[k] : frame'; Replace = 'false ? 0 : frame'; Tests = 'VideoTests' }

        # Streamed timestamps already start at 0 (ffmpeg shifts them); not subtracted again
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '(Streaming ? p : p - _startPts)'; Replace = '(p - _startPts)'; Tests = 'VideoTests' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (seek.Length == 0)'; Replace = 'if (!Streaming && seek.Length == 0)'; Tests = 'TimestampResetTests.AStreamGivesTheFilesCuts' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'long start = Streaming ? MicrosIn(startMicros, timeBaseNum, timeBaseDen) :'; Replace = 'long start = Streaming ? 0 :'; Tests = 'TimestampResetTests.AStreamGivesTheFilesCuts' }

        # Rotation tags: frames arrive upright, so the reported size is swapped
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (Math.Abs(_rotation) % 180 == 90)'; Replace = 'if (false)'; Tests = 'VideoTests' }

        # ContentDetector's flash filter keeps merging until the minimum length passes below the threshold
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = 'if (minLengthMet && !above && MinLengthMet(_lastAbove.Value, _mergeStart!.Value))'; Replace = 'if (minLengthMet && !above)'; Tests = 'DetectorTests' }

        # Several detectors: FrameTimecode equality (exact times only for two timestamps), first of equals kept
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'a.TbDen > 0 && b.TbDen > 0 && a.Fps == b.Fps'; Replace = 'false'; Tests = 'SceneListTests' }

        # --merge-last-scene and --drop-short-scenes
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'scenes[^1].End.Minus(scenes[^1].Start).FrameNum < minSceneLen'; Replace = 'false'; Tests = 'SceneListTests' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'sc.End.Minus(sc.Start).FrameNum < dropShorterThan'; Replace = 'false'; Tests = 'SceneListTests' }

        # detect-content --filter-mode suppress
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = 'if (!(above && minLengthMet))'; Replace = 'if (!above)'; Tests = 'DetectorTests' }

        # Seconds (typed times, scene lists) print their nearest frame's timecode, as FrameTimecode
        @{ File = 'src/ShotDetector/FrameTime.cs'; Find = 'FormatTimecode(TbDen <= 0 ? FrameNum / FpsValue : Seconds)'; Replace = 'FormatTimecode(TbDen is 0 or -1 ? FrameNum / FpsValue : Seconds)'; Tests = 'SceneListTests' }

        # Python rounds half to even
        @{ File = 'src/ShotDetector/FrameTime.cs'; Find = 'if (cmp > 0 || (cmp == 0 && !q.IsEven))'; Replace = 'if (cmp > 0)'; Tests = 'FrameTimeTests' }

        # yadif halves the time base: a seek with deinterlacing compares times, not pts
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'select = ["-vf", _deinterlace'; Replace = 'select = ["-vf", false'; Tests = 'DeinterlaceTests' }

        # MPEG-TS: a seek that lands after the wanted frame (no index) starts over from further back, on both paths
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (emitted == 0 && seek.Length > 1 && SeekLandedLate(pts * liveTimeBase, startFrame))'; Replace = 'if (false)'; Tests = 'RangeTests.SeekingInATransportStreamLosesNoFrames' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (emitted == 0 && !again && next is null && seek.Length > 1)'; Replace = 'if (false)'; Tests = 'RangeTests.SeekingInATransportStreamLosesNoFrames' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'if (emitted == 0 && seeked && (SeekLandedLate(pts * timeBase, startFrame) || (_deinterlace && !before)))'; Replace = 'if (false)'; Tests = 'RangeTests.SeekingInATransportStreamLosesNoFrames' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'late |= seeked && emitted == 0;'; Replace = ''; Tests = 'RangeTests.SeekingInATransportStreamLosesNoFrames' }
        # ... also when it lands under half a frame late, nearer the next frame (a join of concatenated copies)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '|| (startFrame + 1 < Pts.Length && seconds > (Pts[startFrame] + Pts[startFrame + 1]) / 2.0 * _timeBase)'; Replace = ''; Tests = 'RangeTests.SeekingToAJoinCloserThanHalfAFrameLosesNoFrames' }
        # ... and, deinterlacing, when it lands on the wanted frame itself: yadif would have no previous frame for it
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = ' || (_deinterlace && !before)'; Replace = ''; Tests = 'RangeTests.SeekingInterlacedVideoToAKeyframeDeinterlacesAsSequentially' }
        # ... the same through the executable: its select keeps the frame before the wanted one, which must come first
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (keepBefore && emitted == 0)'; Replace = 'if (false)'; Tests = 'RangeTests.SeekingInterlacedVideoToAKeyframeDeinterlacesAsSequentially' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'keepBefore = _deinterlace && early > 0;'; Replace = 'keepBefore = false;'; Tests = 'RangeTests.SeekingInterlacedVideoToAKeyframeDeinterlacesAsSequentially' }

        # MPEG-4 Part 2: the C IDCT (ARM decodes as x86) except where FFmpeg's decoder takes the Xvid IDCT, by its rule:
        # an Xvid signature, or none at all and an Xvid FourCC
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '(codec == "mpeg4" && xvidIdct == false)'; Replace = '(codec == "mpeg4")'; Tests = 'Mpeg4IdctTests' }
        @{ File = 'src/ShotDetector/Mpeg4Idct.cs'; Find = 's.XvidBuild == -1 && s.DivxVersion == -1 && s.LavcBuild == -1 && '; Replace = ''; Tests = 'Mpeg4IdctTests' }

        # DetectionOptions.Warnings: each warning told as soon as it is known, so DetectStreamAsync's callers get them too
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'o.Warnings?.Report(warning);'; Replace = ''; Tests = 'ParallelTests.StreamingCallersGetTheWarningsBeforeTheFirstShot' }

        # The executable's HLS warning: an http playlist too, whose https segments, keys and variants go unchecked as well
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = ' || url.Scheme == Uri.UriSchemeHttp)'; Replace = ')'; Tests = 'UrlOptionTests.AnHttpOrHttpsPlaylistThroughAnExecutableOlderThanFfmpeg9Warns' }
        # ... only through an executable older than FFmpeg 9 (libavformat 63 checks every https connection)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = ' && !(libavformatMajor() >= 63)'; Replace = ''; Tests = 'UrlOptionTests.AnHttpOrHttpsPlaylistThroughAnExecutableOlderThanFfmpeg9Warns' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '!(libavformatMajor() >= 63)'; Replace = '!(libavformatMajor() > 63)'; Tests = 'UrlOptionTests.AnHttpOrHttpsPlaylistThroughAnExecutableOlderThanFfmpeg9Warns' }
        # ... once, though checked again after the frames (Auto may have handed decoding to the executable meanwhile)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (!hlsWarned && '; Replace = 'if ('; Tests = 'VideoTests.UrlInputIsStreamed' }

        # Auto doesn't take an http or https URL the libraries failed to open on to the executable, which would fetch its
        # https segments, keys and variants without checking their certificates (files and other URLs still fall back)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'catch (ShotDetectionException e) when (IsHttp(path))'; Replace = 'catch (ShotDetectionException e) when (false)'; Tests = 'VideoTests.AutoDoesNotHandAnHttpUrlTheLibrariesRefusedToTheExecutable' }

        # Parallel detection: a chunk's reader takes the outer reader's probe instead of probing the file again
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'video.Probed is { } probed ? new VideoReader(path, co, probed, stop.Token) : '; Replace = ''; Tests = 'ParallelTests.ChunksTakeTheOuterReadersProbe' }

        # Frames from the caller (YuvVideo): what a file's result needs and what can't be reproduced without FFmpeg
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (o.Yuv420Converter is null)'; Replace = 'if (false)'; Tests = 'FrameInputTests.NeedsAConverter' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (DeinterlacesFrames(video, o) && o.Yadif is null)'; Replace = 'if (false)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (o.FfmpegResize)'; Replace = 'if (false)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (video.PixelFormat is not ("yuv420p" or "yuvj420p" or "yuv422p" or "yuvj422p" or "yuv420p10le"))'; Replace = 'if (false)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if ((f.Width == 0 ? w : f.Width, f.Height == 0 ? h : f.Height) != (w, h))'; Replace = 'if (false)'; Tests = 'FrameInputTests.AFrameOfAnotherSizeIsRefused' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'new(f.ColorSpace, f.FullRange is { } full ? (full ? "pc" : "tv") : null, f.ChromaLocation)'; Replace = 'new(null, null, f.ChromaLocation)'; Tests = 'FrameInputTests.EachFrameIsConvertedWithItsOwnColours' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = '["chroma_location"] = video.ChromaLocation ?? "",'; Replace = '["chroma_location"] = "",'; Tests = 'FrameInputTests.TenBitChromaIsSitedAsTheFramesSay' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = ': null, f.ChromaLocation);'; Replace = ': null, null);'; Tests = 'FrameInputTests.TenBitChromaIsSitedAsTheFramesSay' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (!TenBit && _codedSize.H % 2 != 0)'; Replace = 'if (_codedSize.H % 2 != 0)'; Tests = 'FrameInputTests.PlanesAreCheckedForTheFormat' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'int BytesPerSample => TenBit ? 2 : 1;'; Replace = 'int BytesPerSample => 1;'; Tests = 'FrameInputTests.PlanesAreCheckedForTheFormat' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'int ChromaHeight => Yuv422 ? _codedSize.H : (_codedSize.H + 1) / 2;'; Replace = 'int ChromaHeight => (_codedSize.H + 1) / 2;'; Tests = 'FrameInputTests.PlanesAreCheckedForTheFormat' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'Line("ChromaLocation", siting);'; Replace = ';'; Tests = 'FrameRangeTests.TheFingerprintCoversEveryOption' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'int given = deinterlaced ? Math.Max(0, (seeded ? seed : firstFrame) - (seeded ? 1 : 2)) : seeded ? seed : lead;'; Replace = 'int given = seeded ? seed : lead;'; Tests = 'FrameRangeTests.DeinterlacedRangesMergeIntoTheSequentialResult' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'var final = VideoReader.DeinterlacesFrames(video, o) ? FinalFrames(list) : list;'; Replace = 'var final = list;'; Tests = 'FrameRangeTests.DeinterlacedRangesMergeIntoTheSequentialResult' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'r.FirstFrame + r.FrameCount >= end ? r : WithoutLastFrame(r)'; Replace = 'WithoutLastFrame(r)'; Tests = 'FrameRangeTests.DeinterlacedRangesMergeIntoTheSequentialResult' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'Joined(VideoReader.DeinterlacesFrames(video, o) ? [.. earlier.Select(WithoutLastFrame)] : earlier)'; Replace = 'Joined(earlier)'; Tests = 'FrameRangeTests.ADeinterlacedRangeTellsTheShotsDetectTells' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'Line(nameof(o.Deinterlace), "frames");'; Replace = ';'; Tests = 'FrameRangeTests.TheFingerprintCoversEveryOption' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'int margin = VideoReader.DeinterlacesFrames(video, (options ?? new DetectionOptions()).Resolved()) ? 2 : 1;'; Replace = 'int margin = 1;'; Tests = 'FrameRangeTests.RangeFramesGiveTheFramesEachRangeNeeds' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = '_frames is not null && !_framesStarted && !_framesEnded && _framesRead < frame + FramesAhead;'; Replace = 'false;'; Tests = 'FrameRangeTests.DeinterlacedRangesMergeIntoTheSequentialResult' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (converts)'; Replace = 'if (true)'; Tests = 'FrameRangeTests.LoadScenesTakesTheVideosPropertiesAndAListAsText' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (converts && YuvForVideo is null)'; Replace = 'if (YuvForVideo is null)'; Tests = 'FrameRangeTests.LoadScenesTakesTheVideosPropertiesAndAListAsText' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'public int EndFrame => FirstFrame + FrameCount - (Deinterlaced && FrameCount > 0 ? 1 : 0);'; Replace = 'public int EndFrame => FirstFrame + FrameCount;'; Tests = 'FrameRangeTests.RunsResumeAtTheEarlierRangesEndFrame' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (stride < width || plane.Length < (long)stride * (height - 1) + width)'; Replace = 'if (stride < width)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (!(Math.Abs(from - expected) <= 0.5 / rate))'; Replace = 'if (false)'; Tests = 'FrameInputTests.FramesMustComeAtTheFrameRate' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = '_startPts = video.StartPts;'; Replace = '_startPts = 0;'; Tests = 'FrameInputTests.FramesAreAtTheirPtsFromTheStreamsStart' }
        # ... turned by the display rotation as OpenCV turns a file's (after converting, flips ignored), any quarter turn
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = '_rotation = video.Rotation;'; Replace = '_rotation = 0;'; Tests = 'FrameInputTests.SameAsTheFileTurned' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'resizer.ResizeYuv(yuv, small, convert, w, h, turn, CropRegion.X, CropRegion.Y);'; Replace = 'resizer.ResizeYuv(yuv, small, convert, w, h, 0, CropRegion.X, CropRegion.Y);'; Tests = 'FrameInputTests.SameAsTheFileTurned' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'var (sx, sy) = CvResize.TurnedSource(turn, CropRegion.X + x, CropRegion.Y + y, w, h);'; Replace = 'var (sx, sy) = CvResize.TurnedSource(180, CropRegion.X + x, CropRegion.Y + y, w, h);'; Tests = 'FrameInputTests.SameAsTheFileTurned' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (video.Rotation % 90 != 0 || Math.Abs(video.Rotation) >= 360)'; Replace = 'if (false)'; Tests = 'FrameInputTests.RotationIsAQuarterTurn' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'Line("Rotation", video.Rotation);'; Replace = ''; Tests = 'FrameInputTests.RangesFromKeyframesMergeIntoTheSequentialResult' }
        # ... at a variable rate with every frame's timestamp given: each at its entry, StartTime as the file path's search
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (entry < 0)'; Replace = 'if (false)'; Tests = 'FrameInputTests.FrameTimestampsAreChecked' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (_framePts[estimate - _frameBase] != entries[estimate])'; Replace = 'if (false)'; Tests = 'FrameInputTests.FrameTimestampsAreChecked' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'double target = seconds - (0.5 / Fps.Value);'; Replace = 'double target = seconds;'; Tests = 'FrameInputTests.SameAsTheFileAtAVariableRate' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'else if (k >= entries.Count || pts[k] != entries[k])'; Replace = 'else if (k >= entries.Count)'; Tests = 'FrameInputTests.RangesAreAtTheirFrameTimestamps' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'Line("FrameTimestamps", video.FrameTimestamps is { } entries ? TimestampsDigest(entries) : null);'; Replace = ''; Tests = 'FrameRangeTests.TheFingerprintCoversEveryOption' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if ((k - startFrame) % (_frameSkip + 1) == 0)'; Replace = 'if ((k - startFrame) % (_frameSkip + 1) == 1)'; Tests = 'FrameInputTests.SameAsTheFile' }
        # ... deinterlaced as FrameDecoder's row-wise yadif: frames k-1, k, k+1 (the first its own previous, the last its own
        # next), each frame's parity from its flags, Auto by the field order, the start taking the first frame's next, ranges refused
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'Deinterlace(_held[Math.Max(k - 1, 0) % 3], cur,'; Replace = 'Deinterlace(cur, cur,'; Tests = 'FrameInputTests.SameAsTheFileDeinterlaced' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'cur, _framesRead > k + 1 ? _held[(k + 1) % 3] : cur, _heldTff[k % 3], yuv'; Replace = 'cur, cur, _heldTff[k % 3], yuv'; Tests = 'FrameInputTests.SameAsTheFileDeinterlaced' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = '_heldTff[_framesRead % 3] = !f.Interlaced || f.TopFieldFirst;'; Replace = '_heldTff[_framesRead % 3] = true;'; Tests = 'FrameInputTests.EachFramesFlagsSetItsParity' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'video.FieldOrder is not (FieldOrder.Progressive or FieldOrder.Unknown)'; Replace = 'false'; Tests = 'FrameInputTests.AutoDeinterlacesWhatTheFieldOrderSays' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'StartEstimate(s) : 0) + FramesAhead;'; Replace = 'StartEstimate(s) : 0);'; Tests = 'FrameInputTests.SameAsTheFileDeinterlaced' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (VideoReader.DeinterlacesFrames(video, o))'; Replace = 'if (false)'; Tests = 'FrameRangeTests.ScoringRefusesWhatARangeCantHonour' }

        # Frames from an asynchronous source (DetectAsync): the start's frames taken one per step, the token checked before each pull
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'while (video.TakesBeforeStart(startSeconds))'; Replace = 'while (false)'; Tests = 'FrameSourceAsyncTests.SameAsTheSynchronousDetect' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'StartEstimate(s) : 0'; Replace = 'StartEstimate(s) - 1 : 0'; Tests = 'FrameSourceAsyncTests.SameAsTheSynchronousDetect' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'cancellationToken.ThrowIfCancellationRequested(); // before each frame is awaited'; Replace = '// mutated'; Tests = 'FrameSourceAsyncTests.CancelsBetweenFrames' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'cancellationToken.ThrowIfCancellationRequested(); // before each frame is pulled'; Replace = '// mutated'; Tests = 'FrameSourceAsyncTests.SynchronousDetectCancelsBeforeEachPull' }
        # The decoding pass for per-frame timestamps (MPEG-PS packets without a pts) stops when cancelled (issue #523):
        # FrameReader's interrupt callback ends a read that waits, and VideoReader hands the pass its token
        @{ File = 'src/ShotDetector/InProcessProbe.cs'; Find = 'InputOptions = Options(inputOptions), CancellationToken = cancellationToken });'; Replace = 'InputOptions = Options(inputOptions) });'; Tests = 'FrameTimestampsTests.InProcessStopsAtAStalledRead' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'InProcessProbe.FrameTimestamps(_path, _ffmpegDirectory, _inputOptions, cancellationToken)'; Replace = 'InProcessProbe.FrameTimestamps(_path, _ffmpegDirectory, _inputOptions)'; Tests = 'FrameTimestampsTests.CancellingDetectionStopsThePass' }
        # In-process, MPEG-PS's timestamps come with the frames (no pass first): looked up over those so far, the pass's
        # once the answer depends on frames to come; the first shot's start taken with the first frame; not deinterlaced
        # (yadif's frames carry other times); only a read that reached the end makes them the whole list
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'return complete ? Math.Min(previous + 1, Math.Max(pts.Length - 1, 0)) : null;'; Replace = 'return Math.Min(previous + 1, Math.Max(pts.Length - 1, 0));'; Tests = 'ArrivingTimestampsTests.APacketTheTimestampsSoFarDontSettleIsUndecided' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'start = first = video.Position(index);'; Replace = '{ }'; Tests = 'ArrivingTimestampsTests.ResultsAreThePass' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '|| _stream is not null || _deinterlace))'; Replace = '|| _stream is not null))'; Tests = 'ArrivingTimestampsTests.DeinterlacingTakesThePass' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'reachedEnd = count is null || emitted < count;'; Replace = 'reachedEnd = true;'; Tests = 'ArrivingTimestampsTests.APartialReadLeavesTheRestToThePass' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = ': _ptsPending && packet < _collectedPts.Count ? _collectedPts[packet]'; Replace = ': false ? 0'; Tests = 'ArrivingTimestampsTests.ResultsAreThePass' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'full.Add(Write(writer, pts + offset, tags, timestamp), stop.Token);'; Replace = 'full.Add(Write(writer, pts + offset, tags, 0), stop.Token);'; Tests = 'ArrivingTimestampsTests.ResultsAreThePass' }
        # Through the executable, from ffprobe's pass alongside the read from the start (its own times are ffmpeg's
        # best-effort ones, extrapolated on frames without pts or dts): the whole list only once the read reached the end,
        # the pass stopped with a read that stops early
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'bool alongside = _ptsPending && startFrame == 0 && restart is null;'; Replace = 'bool alongside = false;'; Tests = 'ArrivingTimestampsTests.TheTimestampsAreThePass' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (alongside && (count is null || emitted < count))'; Replace = 'if (alongside)'; Tests = 'ArrivingTimestampsTests.APartialReadLeavesTheRestToThePass' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (alongside && !again && next is null)'; Replace = 'if (false)'; Tests = 'FrameTimestampsTests.StoppingAReadStopsThePassAlongside' }
        # ShotDetection.Probe's token also stops the stream list, read on first use (a stalled URL)
        @{ File = 'src/ShotDetector/InProcessProbe.cs'; Find = 'new ProbeOptions { InputOptions = Options(inputOptions), CancellationToken = cancellationToken };'; Replace = 'new ProbeOptions { InputOptions = Options(inputOptions) };'; Tests = 'FrameTimestampsTests.StreamsStopAtAStalledRead' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'InProcessProbe.Streams(_path, _ffmpegDirectory, _inputOptions, _cancellationToken)'; Replace = 'InProcessProbe.Streams(_path, _ffmpegDirectory, _inputOptions, CancellationToken.None)'; Tests = 'FrameTimestampsTests.StreamsStopAtAStalledRead' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '"-i", _path], _cancellationToken);'; Replace = '"-i", _path], CancellationToken.None);'; Tests = 'FrameTimestampsTests.StreamsStopAtAStalledRead' }
        # Frame-source detection in ranges (ScoreRange, RangeScores' bytes, Merge): what a range can't honour, ranges that
        # don't make up the video, overlap frames that differ, frames off the constant-rate grid, bytes it can't read
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (firstFrame is < 0 or >= int.MaxValue)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.ScoringRefusesWhatARangeCantHonour' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (k <= firstFrame && firstFrame > 0)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.ScoringRefusesWhatARangeCantHonour' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (o.StartTime is not null || o.EndTime is not null || o.RangeDuration is not null)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.ScoringRefusesWhatARangeCantHonour' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (o.FrameSkip != 0)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.ScoringRefusesWhatARangeCantHonour' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (o.Mode == DetectionMode.Parallel)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.ScoringRefusesWhatARangeCantHonour' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (list[i].Fingerprint != fingerprint)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.MergeRefusesAnotherVideoOrOtherOptions' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (list[0].FirstFrame != 0)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.MergeRefusesRangesThatDontMakeUpTheVideo' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (list.Any(r => r.Metrics.Length != detectors))'; Replace = 'if (false)'; Tests = 'FrameRangeTests.MergeRefusesRangesThatDontMakeUpTheVideo' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (b.FirstFrame <= a.FirstFrame)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.MergeRefusesRangesThatDontMakeUpTheVideo' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (aEnd < b.FirstFrame)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.MergeRefusesRangesThatDontMakeUpTheVideo' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (aEnd == b.FirstFrame)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.MergeRefusesRangesThatDontMakeUpTheVideo' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'long? x = k - aFrom < a.Pts.Length ? a.Pts[k - aFrom] : null, y = k - bFrom < b.Pts.Length ? b.Pts[k - bFrom] : null;'; Replace = 'long? x = 0, y = 0;'; Tests = 'FrameRangeTests.MergeRefusesFramesNotWhereTheirPtsPutThem' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (!Equals(x, y))'; Replace = 'if (false)'; Tests = 'FrameRangeTests.MergeRefusesAnOverlapFrameThatDiffers' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'VideoReader.CheckConstantRate(pts[0], pts[k], k, reader.FramesTimeBase, reader.FramesRate);'; Replace = '_ = k;'; Tests = 'FrameRangeTests.MergeRefusesFramesNotWhereTheirPtsPutThem' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (_framePts.Count > 0 && _frameBase == 0)'; Replace = 'if (_framePts.Count > 0)'; Tests = 'FrameRangeTests.RangesMergeIntoTheSequentialResult' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = '_framesRead = frameBase;'; Replace = '_framesRead = 0;'; Tests = 'FrameRangeTests.RangesMergeIntoTheSequentialResult' }
        # ... and the shots a range tells (OnShot) as it scores its frames: from frame 0, or continuing from the earlier
        # ranges' scores (their shots not told again), whose ranges must lead to it and whose overlap frames must match
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'o.OnShot is not null && (firstFrame == 0 || before is not null)'; Replace = 'o.OnShot is not null && before is not null'; Tests = 'FrameRangeTests.ARangeFromFrameZeroTellsTheShotsDetectTells' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'telling = live is null || k >= live.TellFrom;'; Replace = 'telling = true;'; Tests = 'FrameRangeTests.ARangeContinuingFromEarlierOnesTellsTheRestOfTheShots' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'return firstFrame == 0 ? null : throw'; Replace = 'return true ? null : throw'; Tests = 'FrameRangeTests.AContinuingRangeRefusesEarlierRangesThatDontLeadToIt' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (firstFrame <= start)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.AContinuingRangeRefusesEarlierRangesThatDontLeadToIt' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (end < firstFrame)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.AContinuingRangeRefusesEarlierRangesThatDontLeadToIt' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (end == firstFrame)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.AContinuingRangeRefusesEarlierRangesThatDontLeadToIt' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (x != pts)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.AContinuingRangeRefusesEarlierRangesThatDontLeadToIt' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (!Equals(a, b))'; Replace = 'if (false)'; Tests = 'FrameRangeTests.AContinuingRangeRefusesEarlierRangesThatDontLeadToIt' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'if (bytes.Length < Magic.Length + 2 || !bytes[..Magic.Length].SequenceEqual(Magic))'; Replace = 'if (bytes.Length < Magic.Length + 2)'; Tests = 'FrameRangeTests.FromBytesRefusesBytesItCantRead' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'if (version is not (FormatVersion or FormatVersion1))'; Replace = 'if (false)'; Tests = 'FrameRangeTests.FromBytesRefusesBytesItCantRead' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'if (Checksum(body) != BinaryPrimitives.ReadUInt64LittleEndian(bytes[^8..]))'; Replace = 'if (false)'; Tests = 'FrameRangeTests.FromBytesRefusesBytesItCantRead' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'if (!r.AtEnd)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.FromBytesRefusesBytesItCantRead' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'if (detectors is < 1 or > MaxDetectors)'; Replace = 'if (detectors is < 0 or > MaxDetectors)'; Tests = 'FrameRangeTests.FromBytesRefusesBytesItCantRead' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'if (frameCount > int.MaxValue - 1 - firstFrame)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.FromBytesRefusesBytesItCantRead' }

        # save-images in-process: the executable's one pass (select's n, the same filters, one encoder) through FrameReader;
        # WebP names libwebp, as libwebp_anim (ffmpeg's default where built) wrote one animation for several images
        @{ File = 'src/ShotDetector/Export.cs'; Find = 'byte[] image = encoder.Encode(decoded, vf);'; Replace = 'byte[] image = encoder.Encode(decoded);'; Tests = 'SaveImagesInProcessTests.SameFilesAsTheExecutable'; Project = 'tests/ShotDetector.Tests' }
        @{ File = 'src/ShotDetector/Export.cs'; Find = 'while (more && n <= frame)'; Replace = 'while (more && n < frame)'; Tests = 'SaveImagesInProcessTests.SameFilesAsTheExecutable'; Project = 'tests/ShotDetector.Tests' }
        @{ File = 'src/ShotDetector/Export.cs'; Find = '["-c:v", "libwebp", "-quality",'; Replace = '["-quality",'; Tests = 'VideoTests.WebPImagesThroughTheExecutableAreStills'; Project = 'tests/ShotDetector.Tests' }

        # --loudness: integrated loudness judged on both sides of the target, true peak against its maximum, exit 3 out
        # of range; FrameReader's figures from the filter's own results, which have the last partial 100 ms block's peaks
        @{ File = 'src/ShotDetector.Cli/LoudnessVerdict.cs'; Find = 'Math.Abs(Result.Integrated - Target) <= Tolerance;'; Replace = 'Result.Integrated - Target <= Tolerance;'; Tests = 'CliTests.Loudness' }
        @{ File = 'src/ShotDetector.Cli/LoudnessVerdict.cs'; Find = 'public bool TruePeakPass => Result.TruePeak <= TruePeakMax;'; Replace = 'public bool TruePeakPass => true;'; Tests = 'CliTests.Loudness' }
        @{ File = 'src/ShotDetector.Cli/Program.cs'; Find = 'return verdict.Pass ? 0 : 3;'; Replace = 'return 0;'; Tests = 'CliTests.Loudness' }

        # shotdetect qc: the presets' limits (docs/decisions/2026-11-01-delivery-qc.md), --loudness taking them, the failing
        # checks named and the exit code
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'new("atsc-a85", -24, 2, -2),'; Replace = 'new("atsc-a85", -24, 0.5, -2),'; Tests = 'CliTests.Qc' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'new("streaming-14", -14, 1, -1),'; Replace = 'new("streaming-14", -14, 0.5, -1),'; Tests = 'CliTests.Qc' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'report.Where(c => c.Pass == false)'; Replace = 'report.Where(c => c.Pass == true && false)'; Tests = 'CliTests.Qc' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'return failing.Length == 0 ? 0 : 3;'; Replace = 'return 0;'; Tests = 'CliTests.Qc' }
        @{ File = 'src/ShotDetector.Cli/Program.cs'; Find = 'loudnessTolerance ?? loudnessPreset.Tolerance,'; Replace = 'loudnessTolerance ?? 0.5,'; Tests = 'CliTests.Qc' }
        # ... broadcast range: --brng-max in percent, colour bars frames not judged; bars and tone judged only when required,
        # overlapping in the first minute
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'if (m.IsBars(i))'; Replace = 'if (false && m.IsBars(i))'; Tests = 'CliTests.QcChecks' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'if (m.Brng(i) * 100 > brngMax)'; Replace = 'if (m.Brng(i) > brngMax)'; Tests = 'CliTests.QcChecks' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'require ? lineUp : null'; Replace = 'require ? true : null'; Tests = 'CliTests.QcChecks' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'o.Start < LineUpWindow'; Replace = 'o.Start >= LineUpWindow'; Tests = 'CliTests.QcChecks' }
        # ... black: --black-min given to blackdetect, spans judged only away from the first and last --edge seconds
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'b.Start >= edge && b.End <= end - edge'; Replace = 'b.Start >= 0 && b.End <= end'; Tests = 'CliTests.QcJudgesBlack' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = '            mid.Count == 0,'; Replace = '            true,'; Tests = 'CliTests.QcJudgesBlack' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'MinDuration = TimeSpan.FromSeconds(blackMin)'; Replace = 'MinDuration = TimeSpan.FromSeconds(2)'; Tests = 'CliTests.QcJudgesBlack' }
        # ... freeze: --freeze-min given to freezedetect, judged only away from the edges and not inside black or bars, a
        # still running to the end judged to the video's end
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'black.Any(b => f.Start >= b.Start && End(f) <= b.End)'; Replace = 'false'; Tests = 'CliTests.QcJudgesFreezes' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = '|| bars.Any(b => f.Start >= b.Start.TotalSeconds && End(f) <= b.End.TotalSeconds)'; Replace = '|| false'; Tests = 'CliTests.QcJudgesFreezes' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'f.Start >= edge && End(f) <= end - edge && !Inside(f)'; Replace = '!Inside(f)'; Tests = 'CliTests.QcJudgesFreezes' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'MinDuration = TimeSpan.FromSeconds(freezeMin)'; Replace = 'MinDuration = TimeSpan.FromSeconds(2)'; Tests = 'CliTests.QcJudgesFreezes' }
        @{ File = 'src/ShotDetector.Cli/Qc.cs'; Find = 'f.End ?? end'; Replace = 'f.End ?? double.PositiveInfinity'; Tests = 'CliTests.QcJudgesFreezes' }
        # ... --html: names escaped (the page parses as XML), judged spans marked, times placed on the timeline
        @{ File = 'src/ShotDetector.Cli/QcHtml.cs'; Find = 'WebUtility.HtmlEncode(text)'; Replace = 'text'; Tests = 'CliTests.QcWrites' }
        @{ File = 'src/ShotDetector.Cli/QcHtml.cs'; Find = '(s.Judged ? " judged" : "")'; Replace = '""'; Tests = 'CliTests.QcWrites' }
        @{ File = 'src/ShotDetector.Cli/QcHtml.cs'; Find = 'Math.Clamp(t, 0, duration) / duration * Inner'; Replace = 'Math.Clamp(t, 0, duration) / duration * Width'; Tests = 'CliTests.QcWrites' }

        # --save-shotchanges: Subtitle Edit reads a plain list as seconds only when it has a '.', so whole seconds keep theirs
        @{ File = 'src/ShotDetector/Timeline.cs'; Find = 'result.Cuts.Select(c => c.SecondsText())'; Replace = 'result.Cuts.Select(c => c.Seconds.ToString(CultureInfo.InvariantCulture))'; Tests = 'ShotChangesTests' }

        # --save-youtube-chapters: YouTube's rules (each chapter 10 s or more, the last one too, at least three) and the
        # cut times rounded up from their milliseconds
        @{ File = 'src/ShotDetector/Timeline.cs'; Find = 'if (t - starts[^1] >= minLength)'; Replace = 'if (t - starts[^1] > minLength)'; Tests = 'YouTubeChaptersTests' }
        @{ File = 'src/ShotDetector/Timeline.cs'; Find = 'while (starts.Count > 1 && end - starts[^1] < minLength)'; Replace = 'while (starts.Count > 1 && end - starts[^1] < 0)'; Tests = 'YouTubeChaptersTests' }
        @{ File = 'src/ShotDetector/Timeline.cs'; Find = 'if (starts.Count < 3)'; Replace = 'if (starts.Count < 1)'; Tests = 'YouTubeChaptersTests' }
        @{ File = 'src/ShotDetector/Timeline.cs'; Find = 'Math.Ceiling(Math.Round(cut, 3, MidpointRounding.AwayFromZero))'; Replace = 'Math.Ceiling(cut)'; Tests = 'YouTubeChaptersTests' }
        @{ File = 'src/ShotDetector/Timeline.cs'; Find = 'if (!(minLength >= 10))'; Replace = 'if (minLength < 5)'; Tests = 'YouTubeChaptersTests' }
        @{ File = 'src/ShotDetector/Timeline.cs'; Find = 'if (!(minLength > 0))'; Replace = 'if (minLength < 0)'; Tests = 'YouTubeChaptersTests' }

        # --contact-sheet: save-images' own frames (its options but the size and format), our PNG decoder's row filters,
        # and the PDF's page breaks
        @{ File = 'src/ShotDetector/ContactSheet.cs'; Find = 'FileName = "$SCENE_NUMBER-$IMAGE_NUMBER",'; Replace = 'FileName = "$SCENE_NUMBER-$IMAGE_NUMBER", FrameMargin = 0,'; Tests = 'ContactSheetTests' }
        @{ File = 'src/ShotDetector/ContactSheet.cs'; Find = '4 => Paeth(a, b, c),'; Replace = '4 => a,'; Tests = 'ContactSheetTests' }
        @{ File = 'src/ShotDetector/ContactSheet.cs'; Find = '3 => (a + b) / 2,'; Replace = '3 => (a + b + 1) / 2,'; Tests = 'ContactSheetTests' }
        @{ File = 'src/ShotDetector/ContactSheet.cs'; Find = 'if (pages[^1].Count > 0 && used + heights[r] > room)'; Replace = 'if (pages[^1].Count > 0 && used + heights[r] > room * 10)'; Tests = 'ContactSheetTests' }

        # Black spans on the detection pass, as blackdetect gives them: its minimum duration in time-base units truncated
        # (check_black_end), a run still black at the end ending at its last frame, its time strings
        # (av_ts_make_time_string2), pic_th as at least; the executable's marks applied to the frame they're on, chunks
        # joined without their overlap frame, deinterlaced frames in yadif's time base; FrameReader's threshold and count
        @{ File = 'src/ShotDetector/BlackSpans.cs'; Find = 'long least = (long)(o.MinDuration.TotalSeconds / tb);'; Replace = 'double least = o.MinDuration.TotalSeconds / tb;'; Tests = 'BlackDetectTests.SpansAreBlackdetects' }
        @{ File = 'src/ShotDetector/BlackSpans.cs'; Find = 'Close(n - 1);'; Replace = '{ }'; Tests = 'BlackDetectTests' }
        @{ File = 'src/ShotDetector/BlackSpans.cs'; Find = '(int)-log + 5 : 6'; Replace = '(int)-log + 6 : 6'; Tests = 'BlackDetectTests.TimesPrintAsFfmpegs' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '(double)counted.Black / counted.Pixels >= o.PictureThreshold'; Replace = '(double)counted.Black / counted.Pixels > o.PictureThreshold'; Tests = 'BlackDetectTests' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '_deinterlace ? 2 * pts : pts, '; Replace = 'pts, '; Tests = 'BlackDetectTests' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'mark.Pts <= framePts'; Replace = 'mark.Pts < framePts'; Tests = 'BlackDetectTests' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'int skip = bounds[i] - c.FirstFrame,'; Replace = 'int skip = 0,'; Tests = 'BlackDetectTests' }

        # Freeze spans on the detection pass, as freezedetect gives them: mafd at most n, d reached in microseconds rounded
        # as av_rescale_q does, a start once (the frozen state kept), chroma heights shifted down, one pass; the executable's
        # freeze_start matched to its frame by time
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = ': o.FreezeDetect is not null ? "freeze detection'; Replace = ': false ? "freeze detection'; Tests = 'FreezeDetectTests' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'while (at > 0 && _freezeFrames[at].Mark.Time != start)'; Replace = 'while (false)'; Tests = 'FreezeDetectTests' }
        # ... and its lines read under an older ffmpeg's prefix too (6.1's [freezedetect @, issue #708)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = ' && !line.StartsWith("[freezedetect @ ", StringComparison.Ordinal)'; Replace = ''; Tests = 'FreezeDetectTests.FreezeLinesOfEveryFfmpeg' }
        # ... both timed from the file's start (the earliest stream's, as ffmpeg subtracts it without -copyts), not the video
        # stream's: the executable's from ffmpeg's Input #0 line, in-process from the decoder's StartOffset, frames from the
        # caller from YuvVideo.FileStartPts
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = ': MicrosIn(startMicros, _streamTimeBase.Num, _streamTimeBase.Den) * startScale;'; Replace = ': _startPts * startScale;'; Tests = 'FrameInputTests.SpansCountFromTheFilesStart' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'long spanOffset = decoder.StartOffset;'; Replace = 'long spanOffset = Streaming ? decoder.StartOffset : -_startPts;'; Tests = 'FrameInputTests.SpansCountFromTheFilesStart' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = '_spanOrigin = video.FileStartPts ?? video.StartPts;'; Replace = '_spanOrigin = video.StartPts;'; Tests = 'FrameInputTests.SpansCountFromTheFilesStart' }
        # ... while TrimReferenceTone's times are the video's (frame 0, as scenedetect's StartTime): the tone, timed from
        # the file's start as FrameReader's audio is, moved by how much later the video starts (the file's, and frames
        # from the caller with YuvVideo.FileStartPts)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'Math.Max(0, (t - origin).TotalSeconds)'; Replace = 'Math.Max(0, t.TotalSeconds)'; Tests = 'ReferenceToneTests.OnlyTheEndsAreTrimmed' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'return TimeSpan.FromSeconds((_startPts + decoder.StartOffset) * _timeBase);'; Replace = 'return TimeSpan.Zero;'; Tests = 'FrameInputTests.SameAsTheFileTrimmedToTone' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'FramesToneOrigin => _framesTimed ? TimeSpan.FromSeconds((_startPts - _spanOrigin) * _timeBase) : TimeSpan.Zero;'; Replace = 'FramesToneOrigin => TimeSpan.Zero;'; Tests = 'FrameInputTests.SameAsTheFileTrimmedToTone' }

        # Black and freeze spans for ranges: the flags joined, freezedetect replayed over them at Merge, a range whose
        # comparisons started from another still than the run's refused, the seeded detector kept and its seed frame's
        # flag left out, timed frames checked to start at the still, flags read off the d = 0 spans, the options in the
        # fingerprint, version 1 bytes without them
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (r.FirstFrame > 0 && r.FreezeSeed != reference)'; Replace = 'if (false)'; Tests = 'RangeSpansTests' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = '+ (seeded ? 1 : 0)).Skip(seeded ? 1 : 0)'; Replace = '+ (seeded ? 1 : 0)).Skip(0)'; Tests = 'RangeSpansTests' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = '&& reader.FramePts[0] != earlierPts[given])'; Replace = '&& false)'; Tests = 'RangeSpansTests.ARangeStartingInsideAStillIsRefusedUntilScoredAgain' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'if (o.FreezeDetect is { } freeze)'; Replace = 'if (false && o.FreezeDetect is { } freeze)'; Tests = 'RangeSpansTests.TheFlagsAreInTheFingerprintAndTheBytes' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'for (int i = s.FirstFrame + 1; i < (s.EndFrame ?? frames)'; Replace = 'for (int i = s.FirstFrame; i < (s.EndFrame ?? frames)'; Tests = 'RangeSpansTests' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (!_freezeSeeded)'; Replace = 'if (true)'; Tests = 'RangeSpansTests' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'w.UInt16(flags ? FormatVersion : FormatVersion1);'; Replace = 'w.UInt16(FormatVersion);'; Tests = 'FrameRangeTests' }

        # Frames from the caller that end before their timestamps' last entry end as a file stopped there by its end time
        # does (the last frame's time plus a frame at the average rate, not scenedetect's end of the stream), judged by
        # the last frame's time, so a frame dropped before the end still reads to the end
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (readToEnd && video.FramesStoppedEarly)'; Replace = 'if (false)'; Tests = 'FrameInputTests.FramesStoppedBeforeTheirTimestampsEndAtTheLastOnePlusAFrame' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = '_framePts.Count > 0 && _framePts[^1] < entries[^1]'; Replace = '_framePts.Count > 0 && _framePts.Count < entries.Length'; Tests = 'FrameInputTests.FramesStoppedBeforeTheirTimestampsEndAtTheLastOnePlusAFrame' }
    )
}
