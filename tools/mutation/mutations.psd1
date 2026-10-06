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

        # Streamed input
        @{ File = 'src/ShotDetector/ShotDetection.cs'; Find = 'if (o.StartTime is not null && video.Streaming)'; Replace = 'if (false)'; Tests = 'VideoTests' }

        # Packets without a pts (MPEG-PS): per-frame timestamps from a decoding pass, as OpenCV sees them
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = 'if (missingPts)'; Replace = 'if (false)'; Tests = 'VideoTests' }

        # Rotation tags: frames arrive upright, so the reported size is swapped
        @{ File = 'src/ShotDetector/VideoReader.cs'; Find = '&& Math.Abs(Math.Round(rotation)) % 180 == 90)'; Replace = '&& false)'; Tests = 'VideoTests' }

        # ContentDetector's flash filter keeps merging until the minimum length passes below the threshold
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = 'if (minLengthMet && !above && MinLengthMet(_lastAbove.Value, _mergeStart!.Value))'; Replace = 'if (minLengthMet && !above)'; Tests = 'DetectorTests' }

        # scenedetect 0.6.4's histogram detector re-initialises its last cut at frame 1
        @{ File = 'src/ShotDetector/Detectors.cs'; Find = '(version == PySceneDetectVersion.V0_6_4 && _lastCut == 0)'; Replace = 'false'; Tests = 'HistogramDetectorTests' }

        # Python rounds half to even
        @{ File = 'src/ShotDetector/FrameTime.cs'; Find = 'if (cmp > 0 || (cmp == 0 && !q.IsEven))'; Replace = 'if (cmp > 0)'; Tests = 'FrameTimeTests' }
    )
}
