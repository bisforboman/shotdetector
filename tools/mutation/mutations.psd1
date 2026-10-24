# Mutations for tools/mutation/Invoke-Mutations.ps1: each breaks one guard on purpose (Find must occur exactly once
# in File; Replace takes its place), and at least one test in Tests (a test class filter) must then fail. A mutation
# that survives is a guard no test covers. Add one for every new guard that reproduces a scenedetect quirk.
@{
    Mutations = @(
        # Time range, frame skip and crop (scenedetect's time, --frame-skip, --crop)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'oneBasedFrames: true'; Replace = 'oneBasedFrames: false'; Tests = 'RangeTests' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'endFrame is { } e && frameCount % stride == 0 && '; Replace = 'endFrame is { } e && '; Tests = 'RangeTests' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (o.FrameSkip > 0 && o.CollectStats)'; Replace = 'if (false)'; Tests = 'RangeTests' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'effective = (crop.Width + 1, crop.Height + 1);'; Replace = 'effective = (crop.Width, crop.Height);'; Tests = 'RangeTests' }

        # 10-bit: chroma interpolated from where the frame says it sits, as OpenCV's sws_scale_frame does (executable
        # path: in_chroma_loc; in-process sampled rows: the position sws_scale_frame derives)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '(ChromaLocation is { } cl ? ":in_chroma_loc=" + cl : "")'; Replace = '""'; Tests = 'InProcessTests.SameAsTheFfmpegExecutableForEachFormat' }
        @{ File = 'src/FrameReader/BgrConverter.cs'; Find = 'ffmpeg.av_opt_set_int(_toRows, "src_h_chr_pos", hPos, 0);'; Replace = ''; Tests = 'InProcessTests.SameAsTheFfmpegExecutableForEachFormat' }

        # The first frame's chroma location and colour tags over the stream's (the container's header can contradict the
        # codec's, which OpenCV converts with): executable path's in_chroma_loc, FastYuv's colours and siting
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (key is "chroma_location" or "color_space" or "color_range" && value is not (null or "unspecified" or "unknown" or ""))'; Replace = 'if (false)'; Tests = 'VideoTests.TheFramesChromaLocationAndColourTagsWinOverTheStreams' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'PreferFrameTags(stream, frameTags.Value);'; Replace = '_ = frameTags;'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }

        # ... and in-process, FastYuv converts each frame with its own (a conversion per set of colours), as OpenCV does
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'if (tags == _lastTags)'; Replace = 'if (_lastTags is not null)'; Tests = 'FastYuvTests.InProcessFollowsEachFramesTags' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'PreferFrameTag(stream, "color_space", colorSpace);'; Replace = ''; Tests = 'FastYuvTests.InProcessFollowsEachFramesTags' }

        # ... read only where FastYuv or the executable's scale filter uses them (not the in-process default path), once
        # for a detection's chunks
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'YuvForVideo => _yuv420 is null ? null : _colours.Value.YuvForVideo;'; Replace = 'YuvForVideo => _colours.Value.YuvForVideo;'; Tests = 'InProcessTests.ReadsTheFirstFramesTagsOnlyWhereTheyreUsed' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '_frameTags = probed is not null ? probed.FrameTags'; Replace = '_frameTags = false ? probed!.FrameTags'; Tests = 'InProcessTests.ReadsTheFirstFramesTagsOnlyWhereTheyreUsed' }

        # The executable's raw-YUV pipe labels every frame with the first frame's colour tags, so a matrix or range that
        # changes midway doesn't make ffmpeg convert the later frames' samples (FastYuv reads the decoder's)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'select[1] + "," + _colours.Value.YuvLabels'; Replace = 'select[1]'; Tests = 'FastYuvTests.TagChangeMidwayKeepsTheSamples' }
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
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'const int VRounder = 4;'; Replace = 'const int VRounder = 0;'; Tests = 'FastYuvTests.Converts10BitAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'Math.Clamp(uc >> 19, 0, 255)'; Replace = '((uc >> 19) & 255)'; Tests = 'FastYuvTests.Converts10BitAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'SwscaleFilter.LocalPos(1, hChrPos)'; Replace = 'SwscaleFilter.LocalPos(1, -513)'; Tests = 'FastYuvTests.Converts10BitAsSwscaleOnX64' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'if (width % 2 != 0 || width < 2'; Replace = 'if (width < 2'; Tests = 'FastYuvTests.TenBitDeclinesWhatItDoesNotReproduce' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'h.Size <= cw && v.Size > 2'; Replace = 'h.Size <= cw'; Tests = 'FastYuvTests.TenBitDeclinesWhatItDoesNotReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '_codedSize.W, _codedSize.H, Turn, CropRegion.X'; Replace = '_codedSize.W, _codedSize.H, 0, CropRegion.X'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }

        # In-process YUV pipelines take only the rows the conversion reads (FrameDecoder.CopyRows; with row-wise yadif only
        # those computed): the resize's luma rows (turned: the rows under its pixels) and the chroma rows the converter's
        # taps read (8-bit: its own row's or y / 2; 10-bit: the vertical filter's); whole planes for a converter that doesn't say
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420.cs'; Find = '(chromaHeight == height ? y : y >> 1, 1);'; Replace = '(y >> 1, 1);'; Tests = 'FastYuvTests.RowsOutsideTheRowsReadChangeNothing' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'return (plan.V.Pos[y], plan.V.Size);'; Replace = 'return (plan.V.Pos[y], 1);'; Tests = 'FastYuvTests.RowsOutsideTheRowsReadChangeNothing' }
        @{ File = 'src/ShotDetector/CvResize.cs'; Find = 'TurnedPixels(width, height, turn, cropX, cropY).Select(p => p.Y)'; Replace = 'TurnedPixels(width, height, turn, cropX, cropY).Select(p => p.X)'; Tests = 'FastYuvTests.RowsOutsideTheRowsReadChangeNothing' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '=> YuvForVideo?.ChromaRows is { } chromaRows'; Replace = '=> YuvForVideo?.ChromaRows is var chromaRows'; Tests = 'FastYuvTests.ConvertersThatDoNotSayWhichRowsGetWholePlanes' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = '_rPending.Add((plane, y));'; Replace = ''; Tests = 'InProcessTests.RowWiseYadifEqualsFfmpegs' }

        # Chunked runs: twice the CPUs' decoder threads (at most 16) for frame-threaded decoders up to 1080p, shared out among the chunks
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'frameThreads && pixels <= MoreThreadsMaxPixels ? Math.Clamp(2 * cpus, 1, 16)'; Replace = 'frameThreads && pixels <= MoreThreadsMaxPixels ? Math.Clamp(cpus, 1, 16)'; Tests = 'ParallelTests.ChunkedRunsGiveFrameThreadedDecodersTwiceTheCpus' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'frameThreads && pixels <= MoreThreadsMaxPixels ?'; Replace = 'frameThreads ?'; Tests = 'ParallelTests.ChunkedRunsGiveFrameThreadedDecodersTwiceTheCpus' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '_codedSize.W, _codedSize.H, Turn, CropRegion.X'; Replace = '_codedSize.W, _codedSize.H, 0, CropRegion.X'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }
        # ... a frame at a time (each chroma row filtered once): taken when the converter has it, packed rows x columns,
        # the crop's rows, runs of rows in parallel for chunked outputs
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'c10 as IYuv420P10FrameConverter'; Replace = 'null'; Tests = 'FastYuvTests.PipelinesConvertTenBitAFrameAtATime' }
        @{ File = 'src/ShotDetector.FastYuv/SwscaleYuv420P10.cs'; Find = 'packed ? j * 3 : x * 3;'; Replace = 'packed ? 0 : x * 3;'; Tests = 'FastYuvTests.TenBitFrameAtATimeEqualsRowByRow' }
        @{ File = 'src/ShotDetector/CvResize.cs'; Find = '_frameRows = SourceRows.Select(r => r + cropY).ToArray();'; Replace = '_frameRows = SourceRows.ToArray();'; Tests = 'FastYuvTests.TenBitFrameAtATimeEqualsRowByRow' }
        @{ File = 'src/ShotDetector/CvResize.cs'; Find = 'bgr.AsSpan(first * n, (end - first) * n)'; Replace = 'bgr.AsSpan(0, (end - first) * n)'; Tests = 'FastYuvTests.TenBitFrameAtATimeEqualsRowByRow' }

        # Timestamps starting over (two encodes joined): labelled by time, cuts sorted, stats one row per number
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'seconds < _pts[previous] * _timeBase - tolerance)'; Replace = 'false)'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }
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
        @{ File = 'src/ShotDetector/Stats.cs'; Find = 'if (!byPosition.TryGetValue(at.Key, out var merged))'; Replace = 'if (!byPosition.TryGetValue(at.Key, out var merged) || true)'; Tests = 'ThresholdDetectorTests.StatsAtTheSamePositionMergeKeyByKey' }
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = 'average = stored;'; Replace = '{ }'; Tests = 'ThresholdDetectorTests.RepeatedPositionReusesTheStoredAverageWithStats' }
        @{ File = 'src/ShotDetector/FrameTime.cs'; Find = 'return (Value / g, TbDen / g);'; Replace = 'return (Value, TbDen);'; Tests = 'InProcessTests.JoinedEncodesWithTimestampsStartingOver' }

        # Parallel detection: what a chunk can't reproduce reads sequentially (a frame's position isn't the probe's for its
        # index; a chunk's overlap score or first frame size isn't the next one's)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (k >= a && reader.Position(k).Key != video.Position(k).Key)'; Replace = 'if (false)'; Tests = 'ParallelTests.FallsBackToSequential' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'for (int i = 0; i + 1 < chunks && problem is null; i++)'; Replace = 'for (int i = 0; false; i++)'; Tests = 'ParallelTests.SizeChangeFallsBack' }

        # Parallel detection says why it read sequentially, and shares the decoder threads out without losing one
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = '        fallback = problem;'; Replace = '        fallback = null;'; Tests = 'ParallelTests.FallsBackToSequential' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (o.Mode == DetectionMode.Parallel && notChunked is not null)'; Replace = 'if (false)'; Tests = 'ParallelTests.FallsBackToSequential' }
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
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'IsImageSequencePattern(path) && !IsUrl(path)'; Replace = 'false'; Tests = 'VideoTests.ImageSequenceIsReadAtTheFrameRate' }
        # ... and only a pattern image2 reads as one (av_filename_number_test): a '%' in a file's name (My%20Video.mp4) isn't
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'IsImageSequencePattern(path) && !IsUrl(path)'; Replace = 'path.Contains(''%'') && !IsUrl(path)'; Tests = 'VideoTests.APercentInAFileNameIsNoImageSequence' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (number)'; Replace = 'if (false)'; Tests = 'UrlOptionTests.ImageSequencePatternsAreImage2s' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'else if (path[j] != ''%'')'; Replace = 'else if (false)'; Tests = 'UrlOptionTests.ImageSequencePatternsAreImage2s' }

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
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'if (emitted == 0 && seeked && (SeekLandedLate(pts * timeBase, startFrame) || (_deinterlace && !before)))'; Replace = 'if (false)'; Tests = 'RangeTests.SeekingInATransportStreamLosesNoFrames' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'late |= seeked && emitted == 0;'; Replace = ''; Tests = 'RangeTests.SeekingInATransportStreamLosesNoFrames' }
        # ... also when it lands under half a frame late, nearer the next frame (a join of concatenated copies)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '|| (startFrame + 1 < _pts.Length && seconds > (_pts[startFrame] + _pts[startFrame + 1]) / 2.0 * _timeBase)'; Replace = ''; Tests = 'RangeTests.SeekingToAJoinCloserThanHalfAFrameLosesNoFrames' }
        # ... and, deinterlacing, when it lands on the wanted frame itself: yadif would have no previous frame for it
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = ' || (_deinterlace && !before)'; Replace = ''; Tests = 'RangeTests.SeekingInterlacedVideoToAKeyframeDeinterlacesAsSequentially' }
        # ... the same through the executable: its select keeps the frame before the wanted one, which must come first
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (keepBefore && emitted == 0)'; Replace = 'if (false)'; Tests = 'RangeTests.SeekingInterlacedVideoToAKeyframeDeinterlacesAsSequentially' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'keepBefore = _deinterlace && early > 0;'; Replace = 'keepBefore = false;'; Tests = 'RangeTests.SeekingInterlacedVideoToAKeyframeDeinterlacesAsSequentially' }

        # MPEG-4 Part 2: the C IDCT (ARM decodes as x86) except where FFmpeg's decoder takes the Xvid IDCT, by its rule:
        # an Xvid signature, or none at all and an Xvid FourCC
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '(codec == "mpeg4" && xvidIdct == false)'; Replace = '(codec == "mpeg4")'; Tests = 'Mpeg4IdctTests' }
        @{ File = 'src/ShotDetector/Mpeg4Idct.cs'; Find = 's.XvidBuild == -1 && s.DivxVersion == -1 && s.LavcBuild == -1 && '; Replace = ''; Tests = 'Mpeg4IdctTests' }

        # FrameReader: an InputOptions name nothing took is an ArgumentException (FrameReader's own defaults never count)
        @{ File = 'src/FrameReader/Demuxer.cs'; Find = 'string? unused = ret >= 0 ? UnusedOption(options, listenTimeout) : null;'; Replace = 'string? unused = null;'; Tests = 'InProcessTests.UnknownInputOptionsFailAndShotDetectorsAreTaken' }

        # DetectionOptions.Warnings: each warning told as soon as it is known, so DetectStreamAsync's callers get them too
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'o.Warnings?.Report(warning);'; Replace = ''; Tests = 'ParallelTests.StreamingCallersGetTheWarningsBeforeTheFirstShot' }

        # The executable's HLS warning: an http playlist too, whose https segments, keys and variants go unchecked as well
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = ' || url.Scheme == Uri.UriSchemeHttp)'; Replace = ')'; Tests = 'UrlOptionTests.AnHttpOrHttpsPlaylistThroughTheExecutableWarns' }
        # ... once, though checked again after the frames (Auto may have handed decoding to the executable meanwhile)
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (!hlsWarned && '; Replace = 'if ('; Tests = 'VideoTests.UrlInputIsStreamed' }

        # Auto doesn't take an http or https URL the libraries failed to open on to the executable, which would fetch its
        # https segments, keys and variants without checking their certificates (files and other URLs still fall back)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'catch (ShotDetectionException e) when (IsHttp(path))'; Replace = 'catch (ShotDetectionException e) when (false)'; Tests = 'VideoTests.AutoDoesNotHandAnHttpUrlTheLibrariesRefusedToTheExecutable' }

        # Parallel detection: a chunk's reader takes the outer reader's probe instead of probing the file again
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'video.Probed is { } probed ? new VideoReader(path, co, probed, stop.Token) : '; Replace = ''; Tests = 'ParallelTests.ChunksTakeTheOuterReadersProbe' }

        # Frames from the caller (YuvVideo): what a file's result needs and what can't be reproduced without FFmpeg
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (o.Yuv420Converter is null)'; Replace = 'if (false)'; Tests = 'FrameInputTests.NeedsAConverter' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (o.Deinterlace != DeinterlaceMode.Off || o.Yadif is not null)'; Replace = 'if (false)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (o.FfmpegResize)'; Replace = 'if (false)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (video.PixelFormat is not ("yuv420p" or "yuvj420p"))'; Replace = 'if (false)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (video.Height % 2 != 0)'; Replace = 'if (false)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (stride < width || plane.Length < (long)stride * (height - 1) + width)'; Replace = 'if (stride < width)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (!(Math.Abs(from - expected) <= 0.5 / _framesRate))'; Replace = 'if (false)'; Tests = 'FrameInputTests.FramesMustComeAtTheFrameRate' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = '_startPts = video.StartPts;'; Replace = '_startPts = 0;'; Tests = 'FrameInputTests.FramesAreAtTheirPtsFromTheStreamsStart' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if ((k - startFrame) % (_frameSkip + 1) == 0)'; Replace = 'if ((k - startFrame) % (_frameSkip + 1) == 1)'; Tests = 'FrameInputTests.SameAsTheFile' }
    )
}
