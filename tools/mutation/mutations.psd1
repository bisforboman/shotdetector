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
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '.. _inputOptions, "-noautorotate", "-i", _path,'; Replace = '.. _inputOptions, "-i", _path,'; Tests = 'InProcessTests.RotatesAsTheFfmpegExecutable' }

        # Timestamps starting over (two encodes joined): labelled by time, cuts sorted, stats one row per number
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'seconds < _pts[previous] * _timeBase - tolerance)'; Replace = 'false)'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }
        @{ File = 'src/ShotDetector/Stats.cs'; Find = 'if (!byPosition.TryGetValue(at.Key, out var merged))'; Replace = 'if (!byPosition.TryGetValue(at.Key, out var merged) || true)'; Tests = 'ThresholdDetectorTests.StatsAtTheSamePositionMergeKeyByKey' }
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = 'average = stored;'; Replace = '{ }'; Tests = 'ThresholdDetectorTests.RepeatedPositionReusesTheStoredAverageWithStats' }
        @{ File = 'src/ShotDetector/FrameTime.cs'; Find = 'return (Value / g, TbDen / g);'; Replace = 'return (Value, TbDen);'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }

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
    )
}
