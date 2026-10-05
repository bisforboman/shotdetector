# Third-party notices

ShotDetector contains C# ports of algorithms from the projects below, written so that its results
match theirs exactly. It does not include their source or binaries. At run time it calls an
`ffmpeg`/`ffprobe` you install yourself.

## PySceneDetect (BSD 3-Clause)

Ported: ContentDetector, AdaptiveDetector, ThresholdDetector, FlashFilter, FrameTimecode
arithmetic, scene list / stats / save-images / split-video behaviour
(`Detectors.cs`, `ContentScorer.cs`, `FrameTime.cs`, `Shots.cs`, `Stats.cs`, `Export.cs`,
`ShotDetection.cs`). https://github.com/Breakthrough/PySceneDetect

```
BSD 3-Clause License

Copyright (C) 2014, Brandon Castellano

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## OpenCV (Apache License 2.0)

Ported: `cv2.cvtColor(COLOR_BGR2HSV)` for 8-bit images (`Hsv.cs`), `cv2.resize(INTER_LINEAR)`
(`CvResize.cs`), `cv2.Canny` and `cv2.dilate` (`EdgeDetector.cs`).
Copyright the OpenCV authors. https://github.com/opencv/opencv,
license: https://www.apache.org/licenses/LICENSE-2.0

## FFmpeg libswscale (GNU LGPL 2.1 or later)

Ported: the yuv420p → BGR24 converter (`Yuv420.cs`) from `libswscale/yuv2rgb.c` and
`libswscale/x86/yuv_2_rgb.asm`.
Copyright (C) 2001-2007 Michael Niedermayer, (C) 2009-2010 Konstantin Shishkov.
https://ffmpeg.org, license: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
