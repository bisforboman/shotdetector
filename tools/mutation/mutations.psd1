# Mutations for tools/mutation/Invoke-Mutations.ps1: each breaks one guard on purpose (Find must occur exactly once
# in File; Replace takes its place), and at least one test in Tests (a test class filter) must then fail. A mutation
# that survives is a guard no test covers. Add one for every new guard that reproduces a scenedetect quirk.
@{
    Mutations = @(
        # Time range, frame skip and crop (scenedetect's time, --frame-skip, --crop)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'oneBasedFrames: true'; Replace = 'oneBasedFrames: false'; Tests = 'RangeTests' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'endFrame is { } e && frameCount % stride == 0 && '; Replace = 'endFrame is { } e && '; Tests = 'RangeTests' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (o.FrameSkip > 0 && o.CollectStats)'; Replace = 'if (false)'; Tests = 'RangeTests' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'effective = (CropRegion.Width + 1, CropRegion.Height + 1);'; Replace = 'effective = (CropRegion.Width, CropRegion.Height);'; Tests = 'RangeTests' }

        # 10-bit: chroma interpolated from where the frame says it sits, as OpenCV's sws_scale_frame does (executable
        # path: in_chroma_loc; in-process sampled rows: the position sws_scale_frame derives)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '(_chromaLocation is { } cl ? ":in_chroma_loc=" + cl : "")'; Replace = '""'; Tests = 'InProcessTests.SameAsTheFfmpegExecutableForEachFormat' }
        @{ File = 'src/FrameReader/BgrConverter.cs'; Find = 'ffmpeg.av_opt_set_int(_toRows, "src_h_chr_pos", hPos, 0);'; Replace = ''; Tests = 'InProcessTests.SameAsTheFfmpegExecutableForEachFormat' }

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

        # Rotation as OpenCV's: the converted image turned by the display matrix's angle, not ffmpeg's autorotate
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '.. MaxPixels, "-noautorotate", "-i", _path,'; Replace = '.. MaxPixels, "-i", _path,'; Tests = 'InProcessTests.RotatesAsTheFfmpegExecutable' }

        # FastYuv as x86's swscale: 4:2:2 reads its own row's chroma (YUV2RGB_LOOP's vshift), only where
        # swscale takes the unscaled converter (even decoded height), converted before turning (cv::rotate)
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.cs'; Find = 'RowToBgr(yuv, width, height, height, y, y, cols, bgrRow);'; Replace = 'RowToBgr(yuv, width, height, height, y, y >> 1, cols, bgrRow);'; Tests = 'FastYuvTests' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '!_sizeChanges && (TenBit || _codedSize.H % 2 == 0)'; Replace = '!_sizeChanges'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }

        # FastYuv for 10-bit 4:2:0 as x86's swscale scales it: any height, even widths, MMXEXT's vertical scaler (from
        # vRounder) and output except the last two rows (C, chroma clipped), chroma interpolated from where it sits
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '(TenBit || _codedSize.H % 2 == 0)'; Replace = '(_codedSize.H % 2 == 0)'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'bool asm = y < height - 2;'; Replace = 'bool asm = true;'; Tests = 'FastYuvTests.Converts10BitAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'int ua = 4, va = 4;'; Replace = 'int ua = 0, va = 0;'; Tests = 'FastYuvTests.Converts10BitAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'Math.Clamp(uc >> 19, 0, 255)'; Replace = '((uc >> 19) & 255)'; Tests = 'FastYuvTests.Converts10BitAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'SwscaleFilter.LocalPos(1, hChrPos)'; Replace = 'SwscaleFilter.LocalPos(1, -513)'; Tests = 'FastYuvTests.Converts10BitAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'if (width % 2 != 0 || width < 2'; Replace = 'if (width < 2'; Tests = 'FastYuvTests.TenBitDeclinesWhatItDoesNotReproduce' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'h.Size <= cw && v.Size > 2'; Replace = 'h.Size <= cw'; Tests = 'FastYuvTests.TenBitDeclinesWhatItDoesNotReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '_codedSize.W, _codedSize.H, Turn, CropRegion.X'; Replace = '_codedSize.W, _codedSize.H, 0, CropRegion.X'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '_codedSize.W, _codedSize.H, Turn, CropRegion.X'; Replace = '_codedSize.W, _codedSize.H, 0, CropRegion.X'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }

        # Timestamps starting over (two encodes joined): labelled by time, cuts sorted, stats one row per number
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'seconds < _pts[previous] * _timeBase - tolerance)'; Replace = 'false)'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '&& !(writer.HasStreamSize && decoder.PixelFormat == _pixelFormat);'; Replace = '&& false;'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (yuv && info.NewLayout && _stream is null && !Streaming)'; Replace = 'if (false)'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }

        # A frame in another pixel format than the stream's (4:2:2 and 4:2:0 joined): OpenCV converts it from its own,
        # so FastYuv leaves it to the exact conversion (in-process per frame; the executable reads again on BGR)
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '&& decoder.PixelFormat == _pixelFormat);'; Replace = ');'; Tests = 'InProcessTests.PixelFormatChangeTakesTheExactConversion' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = ' || (format.Success && format.Groups[1].Value != _pixelFormat)'; Replace = ''; Tests = 'InProcessTests.PixelFormatChangeTakesTheExactConversion' }
        @{ File = 'src/ShotDetector/Stats.cs'; Find = 'if (!byPosition.TryGetValue(at.Key, out var merged))'; Replace = 'if (!byPosition.TryGetValue(at.Key, out var merged) || true)'; Tests = 'ThresholdDetectorTests.StatsAtTheSamePositionMergeKeyByKey' }
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = 'average = stored;'; Replace = '{ }'; Tests = 'ThresholdDetectorTests.RepeatedPositionReusesTheStoredAverageWithStats' }
        @{ File = 'src/ShotDetector/FrameTime.cs'; Find = 'return (Value / g, TbDen / g);'; Replace = 'return (Value, TbDen);'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }

        # Parallel detection: what a chunk can't reproduce reads sequentially (a frame's position isn't the probe's for its
        # index; a chunk's overlap score or first frame size isn't the next one's)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (k >= a && reader.Position(k).Key != video.Position(k).Key)'; Replace = 'if (false)'; Tests = 'ParallelTests.FallsBackToSequential' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'for (int i = 0; i + 1 < chunks && problem is null; i++)'; Replace = 'for (int i = 0; false; i++)'; Tests = 'ParallelTests.SizeChangeFallsBack' }

        # Parallel detection says why it read sequentially, and shares the decoder threads out without losing one
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = '        fallback = problem;'; Replace = '        fallback = null;'; Tests = 'ParallelTests.FallsBackToSequential' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'o.Mode == DetectionMode.Parallel && notChunked is not null ?'; Replace = 'false ?'; Tests = 'ParallelTests.FallsBackToSequential' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = ' + (chunk < threads % chunks ? 1 : 0)'; Replace = ''; Tests = 'ParallelTests.DecoderThreadsSharedOut' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'Math.Max(2, 8 / chunks)'; Replace = '8 / chunks'; Tests = 'ParallelTests.FrameBuffersSharedOut' }

        # Auto chunks with half the CPUs (none below 4) and for frames up to 3840 x 2160 (docs/decisions/2026-10-18-parallel-auto-4cpus.md)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'Math.Min(4, cpus / 2)'; Replace = 'Math.Min(4, cpus)'; Tests = 'ParallelTests.AutomaticChunks' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (!forced && pixels > AutoChunksMaxPixels)'; Replace = 'if (false)'; Tests = 'ParallelTests.AutomaticChunksBySizeModeAndThreads' }

        # Streamed input
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (o.StartTime is not null && video.Streaming)'; Replace = 'if (false)'; Tests = 'VideoTests' }

        # Packets without a pts (MPEG-PS): per-frame timestamps from a decoding pass, as OpenCV sees them
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (missingPts)'; Replace = 'if (false)'; Tests = 'VideoTests' }

        # Frame rate override: an image sequence is read at that rate
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'path.Contains(''%'') && !IsUrl(path)'; Replace = 'false'; Tests = 'VideoTests' }

        # Dropped frames: each decoded frame takes its own packet's time (OpenCV labels frames by time)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'k < _livePackets.Count ? _livePackets[k] : frame'; Replace = 'false ? 0 : frame'; Tests = 'VideoTests' }

        # Streamed timestamps already start at 0 (ffmpeg shifts them); not subtracted again
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '(Streaming ? p : p - _startPts)'; Replace = '(p - _startPts)'; Tests = 'VideoTests' }

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
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (emitted == 0 && !again && seek.Length > 1)'; Replace = 'if (false)'; Tests = 'RangeTests.SeekingInATransportStreamLosesNoFrames' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'if (emitted == 0 && seeked && SeekLandedLate(pts * timeBase, startFrame))'; Replace = 'if (false)'; Tests = 'RangeTests.SeekingInATransportStreamLosesNoFrames' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'late |= seeked && emitted == 0;'; Replace = ''; Tests = 'RangeTests.SeekingInATransportStreamLosesNoFrames' }

        # MPEG-4 Part 2: the C IDCT (ARM decodes as x86) except where FFmpeg's decoder takes the Xvid IDCT, by its rule:
        # an Xvid signature, or none at all and an Xvid FourCC
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '(codec == "mpeg4" && xvidIdct == false)'; Replace = '(codec == "mpeg4")'; Tests = 'Mpeg4IdctTests' }
        @{ File = 'src/ShotDetector/Mpeg4Idct.cs'; Find = 's.XvidBuild == -1 && s.DivxVersion == -1 && s.LavcBuild == -1 && '; Replace = ''; Tests = 'Mpeg4IdctTests' }
    )
}
