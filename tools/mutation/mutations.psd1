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

        # 10-bit: chroma interpolated from where the frame says it sits, as OpenCV's sws_scale_frame does (executable
        # path: in_chroma_loc; in-process sampled rows: the position sws_scale_frame derives)
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '(siting is { } cl ? ":in_chroma_loc=" + cl : "")'; Replace = '""'; Tests = 'InProcessTests.SameAsTheFfmpegExecutableForEachFormat' }
        @{ File = 'src/FrameReader/BgrConverter.cs'; Find = 'ffmpeg.av_opt_set_int(_toRows, "src_h_chr_pos", hPos, 0);'; Replace = ''; Tests = 'InProcessTests.SameAsTheFfmpegExecutableForEachFormat' }

        # ... each frame's, where the siting changes midway: the executable reads a file again from the change with the
        # new in_chroma_loc (its scale filter takes one), dropping the earlier encode's frames that share the later one's
        # first times, counting its own frames and keeping the frame labels found so far; a stream says it couldn't
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'bool followSiting = !yuv && _pixelFormat is not ("yuv420p" or "yuvj420p");'; Replace = 'bool followSiting = false;'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'string? siting = restart is null ? ChromaLocation : restart.Location;'; Replace = 'string? siting = ChromaLocation;'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'bool waitingForSiting = restart is not null, stopped = false;'; Replace = 'bool waitingForSiting = false, stopped = false;'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'string[] limit = count is { } n && restart is null ?'; Replace = 'string[] limit = count is { } n ?'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (!Streaming && restart is null)'; Replace = 'if (!Streaming)'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'SitingChangeNotFollowed = true;'; Replace = ';'; Tests = 'VideoTests.TheExecutableFollowsEachFramesChromaSiting' }

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
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'previous = previous with { ChromaLocation = SitingField(line) ?? previous.ChromaLocation });'; Replace = 'previous = previous with { ChromaLocation = null });'; Tests = 'FastYuvTests.ExecutableFollowsEachFramesTags' }
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
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = '_rPending.Add((plane, y));'; Replace = ''; Tests = 'InProcessTests.RowWiseYadifEqualsFfmpegs' }

        # Chunked runs: twice the CPUs' decoder threads (at most 16) for frame-threaded decoders up to 1080p, shared out among the chunks
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'frameThreads && pixels <= MoreThreadsMaxPixels ? Math.Clamp(2 * cpus, 1, 16)'; Replace = 'frameThreads && pixels <= MoreThreadsMaxPixels ? Math.Clamp(cpus, 1, 16)'; Tests = 'ParallelTests.ChunkedRunsGiveFrameThreadedDecodersTwiceTheCpus' }
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'frameThreads && pixels <= MoreThreadsMaxPixels ?'; Replace = 'frameThreads ?'; Tests = 'ParallelTests.ChunkedRunsGiveFrameThreadedDecodersTwiceTheCpus' }
        # ... told by codec name on the executable path, whose FFmpeg libraries may not load
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = 'DecodesInProcess ? FrameReader.FrameDecoder.HasFrameThreads(Codec) : FrameThreadedCodecs.Contains(Codec)'; Replace = 'DecodesInProcess && FrameReader.FrameDecoder.HasFrameThreads(Codec)'; Tests = 'ParallelTests.TellsFrameThreadedDecoders' }
        @{ File = 'src/ShotDetector/VideoReader.InProcess.cs'; Find = '_codedSize.W, _codedSize.H, Turn, CropRegion.X'; Replace = '_codedSize.W, _codedSize.H, 0, CropRegion.X'; Tests = 'FastYuvTests.SameFramesAsTheDefaultPath' }
        # ... a frame at a time (each chroma row filtered once): taken when the converter has it, packed rows x columns,
        # the crop's rows, runs of rows in parallel for chunked outputs
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'c10 as IYuv420P10FrameConverter'; Replace = 'null'; Tests = 'FastYuvTests.PipelinesConvertTenBitAFrameAtATime' }
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
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'framePts -= MicrosIn(startMicros, timeBaseNum, timeBaseDen);'; Replace = 'framePts -= 0;'; Tests = 'TimestampResetTests.AStreamGivesTheFilesCuts' }

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
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (DeinterlacesFrames(video, o) && o.Yadif is null)'; Replace = 'if (false)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (o.FfmpegResize)'; Replace = 'if (false)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (video.PixelFormat is not ("yuv420p" or "yuvj420p"))'; Replace = 'if (false)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (video.Height % 2 != 0)'; Replace = 'if (false)'; Tests = 'FrameInputTests.RejectsWhatItCantReproduce' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if ((f.Width == 0 ? w : f.Width, f.Height == 0 ? h : f.Height) != (w, h))'; Replace = 'if (false)'; Tests = 'FrameInputTests.AFrameOfAnotherSizeIsRefused' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'new(f.ColorSpace, f.FullRange is { } full ? (full ? "pc" : "tv") : null, f.ChromaLocation)'; Replace = 'new(null, null, f.ChromaLocation)'; Tests = 'FrameInputTests.EachFrameIsConvertedWithItsOwnColours' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = '["chroma_location"] = video.ChromaLocation ?? "",'; Replace = '["chroma_location"] = "",'; Tests = 'FrameInputTests.TenBitChromaIsSitedAsTheFramesSay' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = ': null, f.ChromaLocation);'; Replace = ': null, null);'; Tests = 'FrameInputTests.TenBitChromaIsSitedAsTheFramesSay' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'if (video.Height % 2 != 0 && video.PixelFormat != "yuv420p10le")'; Replace = 'if (video.Height % 2 != 0)'; Tests = 'FrameInputTests.PlanesAreCheckedForTheFormat' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'int BytesPerSample => TenBit ? 2 : 1;'; Replace = 'int BytesPerSample => 1;'; Tests = 'FrameInputTests.PlanesAreCheckedForTheFormat' }
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = 'int ChromaHeight => Yuv422 ? _codedSize.H : (_codedSize.H + 1) / 2;'; Replace = 'int ChromaHeight => (_codedSize.H + 1) / 2;'; Tests = 'FrameInputTests.PlanesAreCheckedForTheFormat' }
        @{ File = 'src/ShotDetector/ShotDetection.Ranges.cs'; Find = 'Line("ChromaLocation", siting);'; Replace = ';'; Tests = 'FrameRangeTests.TheFingerprintCoversEveryOption' }
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
        @{ File = 'src/ShotDetector/VideoReader.Frames.cs'; Find = '_framesRead > k + 1 ? _held[(k + 1) % 3] : cur'; Replace = 'cur'; Tests = 'FrameInputTests.SameAsTheFileDeinterlaced' }
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
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'if (version != FormatVersion)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.FromBytesRefusesBytesItCantRead' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'if (Checksum(body) != BinaryPrimitives.ReadUInt64LittleEndian(bytes[^8..]))'; Replace = 'if (false)'; Tests = 'FrameRangeTests.FromBytesRefusesBytesItCantRead' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'if (!r.AtEnd)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.FromBytesRefusesBytesItCantRead' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'if (detectors is < 1 or > MaxDetectors)'; Replace = 'if (detectors is < 0 or > MaxDetectors)'; Tests = 'FrameRangeTests.FromBytesRefusesBytesItCantRead' }
        @{ File = 'src/ShotDetector/RangeScores.cs'; Find = 'if (frameCount > int.MaxValue - 1 - firstFrame)'; Replace = 'if (false)'; Tests = 'FrameRangeTests.FromBytesRefusesBytesItCantRead' }

        # FrameReader: the tone and waveform from samples refuse what they can't take (issue #494)
        @{ File = 'src/FrameReader/ReferenceToneFinder.cs'; Find = 'ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);'; Replace = ''; Tests = 'AudioFromSamplesTests.Refusals'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ReferenceToneFinder.cs'; Find = 'ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);'; Replace = ''; Tests = 'AudioFromSamplesTests.Refusals'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ReferenceToneFinder.cs'; Find = 'if (interleaved.Length % _channels != 0)'; Replace = 'if (false)'; Tests = 'AudioFromSamplesTests.Refusals'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ReferenceToneFinder.cs'; Find = 'throw new InvalidOperationException("Finish was called: start a new finder.");'; Replace = '{ }'; Tests = 'AudioFromSamplesTests.Refusals'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/WaveformBuilder.cs'; Find = 'ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);'; Replace = ''; Tests = 'AudioFromSamplesTests.Refusals'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/WaveformBuilder.cs'; Find = 'ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);'; Replace = ''; Tests = 'AudioFromSamplesTests.Refusals'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/WaveformBuilder.cs'; Find = 'if (interleaved.Length % _inChannels != 0)'; Replace = 'if (false)'; Tests = 'AudioFromSamplesTests.Refusals'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/WaveformBuilder.cs'; Find = 'throw new InvalidOperationException("Finish was called: start a new builder.");'; Replace = '{ }'; Tests = 'AudioFromSamplesTests.Refusals'; Project = 'tests/FrameReader.Tests' }

        # FrameReader: a whole-file job's token reaches FFmpeg's interrupt callback, so a waiting network read stops (issue #522)
        @{ File = 'src/FrameReader/Audio.cs'; Find = 'InputOptions = o.InputOptions, CancellationToken = cancellationToken }'; Replace = 'InputOptions = o.InputOptions }'; Tests = 'CancellationTests.AudioConvertStopsWhileWaitingForANetworkRead'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/Video.cs'; Find = 'new ProbeOptions { CancellationToken = cancellationToken }'; Replace = 'null'; Tests = 'CancellationTests.VideoTranscodeStopsWhileWaitingForANetworkRead'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/Video.cs'; Find = 'Deinterlace = deinterlace, CancellationToken = cancellationToken }'; Replace = 'Deinterlace = deinterlace }'; Tests = 'CancellationTests.VideoTranscodeStopsWhileWaitingForANetworkRead'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/Video.cs'; Find = 'new AudioReaderOptions { CancellationToken = cancellationToken }'; Replace = 'null'; Tests = 'CancellationTests.VideoTranscodeStopsWhileWaitingForANetworkRead'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/MediaProbe.cs'; Find = 'options = (options ?? new ProbeOptions()) with { CancellationToken = cancellationToken };'; Replace = '{ }'; Tests = 'CancellationTests.ProbeJsonStopsWhileWaitingForANetworkRead'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/Demuxer.cs'; Find = 'if (cancellationToken.IsCancellationRequested)'; Replace = 'if (false)'; Tests = 'CancellationTests.ProbeJsonStopsWhileWaitingForANetworkRead'; Project = 'tests/FrameReader.Tests' }
        # An MP4/MOV packet scan reads the sample tables, not the media data (FileInput): only for codecs whose packets come out
        # the same without their data, not fragmented files, nothing past the file's end
        @{ File = 'src/FrameReader/MediaProbe.cs'; Find = '&& TimestampsWithoutData.Contains(fmt->streams[index]->codecpar->codec_id);'; Replace = '&& true;'; Tests = 'PacketScanTests.TimestampsWithoutMediaDataAreTheSame'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FileInput.cs'; Find = 'if (type is "moof" or "mfra" or "sidx")'; Replace = 'if (false)'; Tests = 'PacketScanTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FileInput.cs'; Find = 'result.Add((at + headerSize, Math.Min(at + size, length)));'; Replace = 'result.Add((at + headerSize, at + size));'; Tests = 'PacketScanTests'; Project = 'tests/FrameReader.Tests' }
        # FrameReader: MaxWidth is ffmpeg's scale='min(N,iw)':-2 (issue #542): never upscaled, the height rounded to the
        # nearest even number (ties up), 0 filled in with the input's; not with Width and Height
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'int outWidth = Math.Min(max, width);'; Replace = 'int outWidth = max;'; Tests = 'VideoFrameReaderTests.MaxWidthIsFfmpegsMinWidthAndEvenHeight'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'int outHeight = (int)((outWidth * (long)height + twice / 2) / twice * 2);'; Replace = 'int outHeight = (int)(outWidth * (long)height / twice * 2);'; Tests = 'VideoFrameReaderTests.MaxWidthIsFfmpegsMinWidthAndEvenHeight'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'return (outWidth, outHeight == 0 ? height : outHeight);'; Replace = 'return (outWidth, outHeight);'; Tests = 'VideoFrameReaderTests.MaxWidthIsFfmpegsMinWidthAndEvenHeight'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = '(max <= 0 || _options.Width is not null)'; Replace = '(max <= 0)'; Tests = 'VideoFrameReaderTests.FrameTimesStartAtZeroAndSizesMustComeInPairs'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = '(max <= 0 || _options.Width is not null)'; Replace = '(_options.Width is not null)'; Tests = 'VideoFrameReaderTests.FrameTimesStartAtZeroAndSizesMustComeInPairs'; Project = 'tests/FrameReader.Tests' }

        # save-images in-process: the executable's one pass (select's n, the same filters, one encoder) through FrameReader;
        # WebP names libwebp, as libwebp_anim (ffmpeg's default where built) wrote one animation for several images
        @{ File = 'src/ShotDetector/Export.cs'; Find = 'byte[] image = encoder.Encode(decoded, vf);'; Replace = 'byte[] image = encoder.Encode(decoded);'; Tests = 'SaveImagesInProcessTests.SameFilesAsTheExecutable'; Project = 'tests/ShotDetector.Tests' }
        @{ File = 'src/ShotDetector/Export.cs'; Find = 'while (more && n <= frame)'; Replace = 'while (more && n < frame)'; Tests = 'SaveImagesInProcessTests.SameFilesAsTheExecutable'; Project = 'tests/ShotDetector.Tests' }
        @{ File = 'src/ShotDetector/Export.cs'; Find = '["-c:v", "libwebp", "-quality",'; Replace = '["-quality",'; Tests = 'VideoTests.WebPImagesThroughTheExecutableAreStills'; Project = 'tests/ShotDetector.Tests' }

        # FrameReader's ImageEncoder (issue #542): the file ffmpeg writes from the same raw frame and tags; JPEG's rate control
        # afresh for each image; options only for their format; the frame's aspect ratio and tags as ffmpeg's frame has them
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = 'if (_o.Format == ImageFormat.Jpg && _o.JpegQscale is null)'; Replace = 'if (false)'; Tests = 'ImageEncoderTests.EachFrameOfASequenceIsAOneFrameRun'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = 'if (_graph == null || !input.SameGraph(_graphFor))'; Replace = 'if (_graph == null)'; Tests = 'ImageEncoderTests.EachFrameOfASequenceIsAOneFrameRun'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '_filtered->quality = q * ffmpeg.FF_QP2LAMBDA;'; Replace = '{ }'; Tests = 'ImageEncoderTests.EachFrameOfASequenceIsAOneFrameRun'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '_enc->compression_level = c;'; Replace = ''; Tests = 'ImageEncoderTests.BytesAreFfmpegsFromTheRawFrame'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '_enc->thread_count = 1;'; Replace = '_enc->thread_count = 4;'; Tests = 'ImageEncoderTests.EachFrameOfASequenceIsAOneFrameRun'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '_frame->color_primaries = input.Primaries;'; Replace = ''; Tests = 'ImageEncoderTests.BytesAreFfmpegsFromTheRawFrame'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = 'Check(ffmpeg.av_opt_set_double(_enc->priv_data, "quality", wq, 0), "quality");'; Replace = '{ }'; Tests = 'ImageEncoderTests.BytesAreFfmpegsFromTheRawFrame'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = 'Check(ffmpeg.av_opt_set_int(_enc->priv_data, "lossless", 1, 0), "lossless");'; Replace = '{ }'; Tests = 'ImageEncoderTests.WritesWithTheseLibraries'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '_enc->compression_level = wc;'; Replace = '{ }'; Tests = 'ImageEncoderTests.BytesAreFfmpegsFromTheRawFrame'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '(_o.Format != ImageFormat.WebP || !(wq >= 0 && wq <= 100))'; Replace = '(!(wq >= 0 && wq <= 100))'; Tests = 'ImageEncoderTests.RefusesWhatItCantWrite'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '(_o.Format != ImageFormat.WebP || !(wq >= 0 && wq <= 100))'; Replace = '(_o.Format != ImageFormat.WebP)'; Tests = 'ImageEncoderTests.RefusesWhatItCantWrite'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '(_o.Format != ImageFormat.WebP || !(wq >= 0 && wq <= 100))'; Replace = '(_o.Format != ImageFormat.WebP || wq is < 0 or > 100)'; Tests = 'ImageEncoderTests.RefusesWhatItCantWrite'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = 'if (_o.WebPLossless && _o.Format != ImageFormat.WebP)'; Replace = 'if (false)'; Tests = 'ImageEncoderTests.RefusesWhatItCantWrite'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '(_o.Format != ImageFormat.WebP || wc is < 0 or > 6)'; Replace = '(wc is < 0 or > 6)'; Tests = 'ImageEncoderTests.RefusesWhatItCantWrite'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '(_o.Format != ImageFormat.WebP || wc is < 0 or > 6)'; Replace = '(_o.Format != ImageFormat.WebP)'; Tests = 'ImageEncoderTests.RefusesWhatItCantWrite'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '(_o.Format != ImageFormat.Jpg || q is < 2 or > 31)'; Replace = '(q is < 2 or > 31)'; Tests = 'ImageEncoderTests.RefusesWhatItCantWrite'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '(_o.Format != ImageFormat.Jpg || q is < 2 or > 31)'; Replace = '(_o.Format != ImageFormat.Jpg)'; Tests = 'ImageEncoderTests.RefusesWhatItCantWrite'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '(_o.Format != ImageFormat.Png || c is < 0 or > 9)'; Replace = '(c is < 0 or > 9)'; Tests = 'ImageEncoderTests.RefusesWhatItCantWrite'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '(_o.Format != ImageFormat.Png || c is < 0 or > 9)'; Replace = '(_o.Format != ImageFormat.Png)'; Tests = 'ImageEncoderTests.RefusesWhatItCantWrite'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = '? ffmpeg.av_mul_q(new AVRational { num = height * src->width, den = width * src->height }, sar)'; Replace = '? sar'; Tests = 'ImageEncoderTests.TagsAreFfmpegs'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'var tags = _options.FrameFormat == FrameFormat.Gray8'; Replace = 'var tags = false'; Tests = 'ImageEncoderTests.TagsAreFfmpegs'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = 'into->sample_aspect_ratio = _sar;'; Replace = '{ }'; Tests = 'ImageEncoderTests.TagsAreFfmpegs'; Project = 'tests/FrameReader.Tests' }

        # ImageEncoder from a decoded frame: ffmpeg's one command's graph (the -vf before the conversion), the decoder's bits per
        # raw sample only through a metadata-only graph (else a 10-bit PNG gains an sBIT chunk), and the filter options' rules
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = 'OpenEncoder(_filtered, _metaOnly ? decoded?.BitsPerRawSample ?? 0 : 0);'; Replace = 'OpenEncoder(_filtered, decoded?.BitsPerRawSample ?? 0);'; Tests = 'ImageEncoderTests.DecodedFramesAreFfmpegsOneCommand'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = 'filter = input.Filter + "," + filter;'; Replace = 'filter = "null," + filter;'; Tests = 'ImageEncoderTests.DecodedFramesAreFfmpegsOneCommand'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '(max <= 0 || Width is not null)'; Replace = '(Width is not null)'; Tests = 'ImageEncoderTests.RefusesFiltersThatDontGoTogether'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = 'if (Width is null != Height is null || Width <= 0 || Height <= 0)'; Replace = 'if (Width <= 0 || Height <= 0)'; Tests = 'ImageEncoderTests.RefusesFiltersThatDontGoTogether'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = '(string.IsNullOrWhiteSpace(Filter) || Width is not null || MaxWidth is not null)'; Replace = '(string.IsNullOrWhiteSpace(Filter))'; Tests = 'ImageEncoderTests.RefusesFiltersThatDontGoTogether'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ImageEncoder.cs'; Find = 'if (parsed < 0 && input.Filter is not null)'; Replace = 'if (false)'; Tests = 'ImageEncoderTests.RefusesFiltersThatDontGoTogether'; Project = 'tests/FrameReader.Tests' }

        # FrameReader: VideoFrameReader's ReadAhead gives the frames reading in the read gives (issue #566): what was read
        # ahead goes at a seek or a jump, every frame taken is let go, and a picture a second never stops the thread
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = '_ahead?.Clear(); // the frames read ahead are the old position''s'; Replace = ''; Tests = 'ReadAheadTests.FramesAreThoseWithout'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = '_ahead?.Clear(); // what was read ahead is before the jump'; Replace = ''; Tests = 'ReadAheadTests.FramesAreThoseWithout'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'if (target > from && Pause()'; Replace = 'if (Pause()'; Tests = 'ReadAheadTests.ReadingEverySecondDoesntStopTheThread'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'if (_options.ReadAhead < 0)'; Replace = 'if (false)'; Tests = 'ReadAheadTests.ReadAheadIsZeroOrMore'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ReadAhead.cs'; Find = 'DropFrames(); // those from before the seek'; Replace = ''; Tests = 'ReadAheadTests.FramesAreThoseWithout'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/ReadAhead.cs'; Find = 'DropFrames(); // all, the thread gone'; Replace = ''; Tests = 'ReadAheadTests.DisposeStopsTheThreadMidFrame'; Project = 'tests/FrameReader.Tests' }

        # FrameReader: VideoFrameReader's takes (issue #574): a taken frame is the caller's own reference (read ahead, not
        # the queue's, which the next read lets go), its time is the reads' (shifted by the start), and FrameRate refuses them
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'return _ahead.Current.Frame.Reference(Pts, time);'; Replace = 'return _ahead.Current.Frame;'; Tests = 'VideoFrameReaderTakeTests.TakenFramesOutliveReadsAndTheReader'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = 'TimeSpan.FromSeconds((pts + StartOffset) * TimeBase.Value);'; Replace = 'TimeSpan.FromSeconds(pts * TimeBase.Value);'; Tests = 'VideoFrameReaderTakeTests.TakesAreTheDecodersFramesWhereTheReadsAre'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'NoFrameRate(nameof(TryTakeForwardTo));'; Replace = ''; Tests = 'VideoFrameReaderTakeTests.FrameRateTakesAreTheFpsFiltersFrames'; Project = 'tests/FrameReader.Tests' }

        # ... with FrameRate, TryTake gives the fps filter's frames (issue #574's follow-up)
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'frame = NextFps() ? TakeFps(rate) : null;'; Replace = 'frame = Advance() ? Take() : null;'; Tests = 'VideoFrameReaderTakeTests.FrameRateTakesAreTheFpsFiltersFrames'; Project = 'tests/FrameReader.Tests' }

        # FrameReader: a frame without a timestamp (MPEG-PS's and AVI's last) gets ffmpeg's made-up one, the previous frame's
        # plus its duration; a 90-degree turn of 4:2:2 (not 4:2:2 once transposed) is refused, as CanRotate says
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'pts = _lastPts == ffmpeg.AV_NOPTS_VALUE ? 0 : _lastPts + _lastDuration;'; Replace = '{ }'; Tests = 'VideoFrameReaderTests.AFrameWithoutATimestampGetsFfmpegsTime'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'if (duration > 0 && !unreliable && !(duration == 1 && diff > 2))'; Replace = 'if (false)'; Tests = 'VideoFrameReaderTests.AFrameWithoutATimestampGetsFfmpegsTime'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = '|| (t && desc->log2_chroma_w != desc->log2_chroma_h)'; Replace = ''; Tests = 'FrameDecoderTests.DeinterlacesRotatesAndReadsStreams'; Project = 'tests/FrameReader.Tests' }

        # FrameReader: packets of frames nothing references, before the wanted frame, are left out of the decoder, and every
        # frame given is the full decode's: only such packets; after a seek H.264 only from an IDR; an SPS that's
        # field-coded or without a reorder depth, DivX packed B-frames, interlaced MPEG-2, packets without a pts turn it off;
        # HEVC's only of the top temporal layer; the seek's and ReadAt's next targets
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = '|| !u.Unreferenced(new ReadOnlySpan<byte>(pkt->data, pkt->size))'; Replace = ''; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/UnreferencedPackets.cs'; Find = '&& _h264Ready && _afterIdr;'; Replace = '&& _h264Ready;'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/UnreferencedPackets.cs'; Find = '_off |= !(_h264Ready = ready);'; Replace = '_h264Ready = true;'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/UnreferencedPackets.cs'; Find = "if (p[j] == 'p')"; Replace = 'if (false)'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/UnreferencedPackets.cs'; Find = '&& tid == _hevcTopLayer'; Replace = ''; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/UnreferencedPackets.cs'; Find = 'AVCodecID.AV_CODEC_ID_MPEG2VIDEO when progressive'; Replace = 'AVCodecID.AV_CODEC_ID_MPEG2VIDEO'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = 'if (pkt->pts == ffmpeg.AV_NOPTS_VALUE)'; Replace = 'if (false)'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = '_decoder.SkipBefore = -_keepFrom.Value;'; Replace = '_decoder.SkipBefore = long.MaxValue;'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'i + 1 < to ? times[i + 1] : null'; Replace = 'i + 1 < to ? times[^1] : null'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'if (!_decoder.LeftOut(wanted, got ? Pts : long.MaxValue))'; Replace = 'if (true)'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'if (_current && _guessed)'; Replace = 'if (false)'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        # MPEG-TS reads again (its keyframes read, by pts), by the byte position of the one at or before the time (its seeks
        # by timestamp land a GOP late), and from the one before that when the check fails
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = '_keyframes = format == "mpegts" && io == null ? [] : null;'; Replace = '_keyframes = null;'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = 'Check(TrySeek(pos, ffmpeg.AVSEEK_FLAG_BYTE), "seek");'; Replace = 'Seek(pts);'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = 'return i >= 0 ? i : ~i - 1;'; Replace = 'return i >= 0 ? i : ~i;'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = '_decoder.ReadAgainFrom(Math.Max(0, key - 1)); // MPEG-TS'; Replace = '_decoder.Seek(Math.Max(0, key - 1)); //'; Tests = 'SkipUnreferencedTests'; Project = 'tests/FrameReader.Tests' }

        # FrameReader: no keyframe jumps reading forward in MPEG-PS (its index marks every PES as a keyframe), AVI with
        # B-frames (I- and P-frames timed by libavcodec's guess) or raw streams; a seek the container refuses reads from the
        # start; raw streams timed as ffmpeg (their timestamps dropped), seeks there decode from the start (or on), and
        # taken frames carry the reader's times; a jump that lands past the time (an index by dts) from the keyframe before
        # (AVI's guard has no line: a jump there that goes wrong lands past the time, so the check after it catches it too)
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = 'format == "mpeg" || '; Replace = ''; Tests = 'SeekDemuxerTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = 'if (!TrySeekMicroseconds(point))'; Replace = 'if (false)'; Tests = 'SeekDemuxerTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = 'if (!TrySeekMicroseconds(start) && _started)'; Replace = 'if (_started)'; Tests = 'SeekDemuxerTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = '_noTimestamps = (_fmt->iformat->flags & ffmpeg.AVFMT_NOTIMESTAMPS) != 0 && wallclock == 0;'; Replace = '_noTimestamps = false;'; Tests = 'SeekDemuxerTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = ' || _noTimestamps;'; Replace = ';'; Tests = 'SeekDemuxerTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'long pts = _decoder.TimestampsUnreliable ? ffmpeg.AV_NOPTS_VALUE : _ahead?.Current.Pts ?? _decoder.Pts;'; Replace = 'long pts = _ahead?.Current.Pts ?? _decoder.Pts;'; Tests = 'SeekDemuxerTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'if (_decoder.TimestampsUnreliable)'; Replace = 'if (false)'; Tests = 'SeekDemuxerTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = '_decoder.Rewind();'; Replace = ''; Tests = 'SeekDemuxerTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'bool past = first && Pts + keep > 0;'; Replace = 'bool past = false;'; Tests = 'SeekDemuxerTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/VideoFrameReader.cs'; Find = 'return taken.Reference(Pts, time);'; Replace = 'return taken.Reference();'; Tests = 'SeekDemuxerTests'; Project = 'tests/FrameReader.Tests' }
        @{ File = 'src/FrameReader/FrameDecoder.cs'; Find = 'long point = Demuxer.SeekPoint(_fmt, timestamp);'; Replace = 'long point = Demuxer.SeekPoint(_fmt, timestamp); SeekMicroseconds(point);'; Tests = 'SeekDemuxerTests'; Project = 'tests/FrameReader.Tests' }

        # --loudness: integrated loudness judged on both sides of the target, true peak against its maximum, exit 3 out
        # of range; FrameReader's figures from the filter's own results, which have the last partial 100 ms block's peaks
        @{ File = 'src/ShotDetector.Cli/LoudnessVerdict.cs'; Find = 'Math.Abs(Result.Integrated - Target) <= Tolerance;'; Replace = 'Result.Integrated - Target <= Tolerance;'; Tests = 'CliTests.Loudness' }
        @{ File = 'src/ShotDetector.Cli/LoudnessVerdict.cs'; Find = 'public bool TruePeakPass => Result.TruePeak <= TruePeakMax;'; Replace = 'public bool TruePeakPass => true;'; Tests = 'CliTests.Loudness' }
        @{ File = 'src/ShotDetector.Cli/Program.cs'; Find = 'return verdict.Pass ? 0 : 3;'; Replace = 'return 0;'; Tests = 'CliTests.Loudness' }
        @{ File = 'src/FrameReader/Loudness.cs'; Find = 'Get("sample_peak"), Get("true_peak")'; Replace = 'Get("sample_peak") - 0.05, Get("true_peak")'; Tests = 'LoudnessTests'; Project = 'tests/FrameReader.Tests' }
    )
}
