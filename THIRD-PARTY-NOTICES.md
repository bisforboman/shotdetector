# Third-party notices

ShotDetector contains C# ports of algorithms from the projects below, written so that its results
match theirs exactly. At run time it calls an `ffmpeg`/`ffprobe` you install yourself, and loads FFmpeg's
shared libraries when they are there.

The ShotDetector library (MIT) contains ports from PySceneDetect and OpenCV only. The FFmpeg port lives in
the separate ShotDetector.FastYuv package, which is LGPL-2.1-or-later. FFmpeg's own libraries, built by us
for decoding only, ship in the ShotDetector.Native.<rid> packages and are bundled with the shotdetect CLI
(tool and binaries); see the last section.

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

## simpletable (MIT), as bundled in PySceneDetect

`Shots.Html` reproduces the HTML (markup and default CSS) that PySceneDetect's save-html writes with
its bundled copy of simpletable (`scenedetect/_thirdparty/simpletable.py`).

```
The MIT License (MIT)

Copyright (c) 2014 Matheus Vieira Portela

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## OpenCV (Apache License 2.0)

Ported: `cv2.cvtColor(COLOR_BGR2HSV)` for 8-bit images (`Hsv.cs`), `cv2.resize(INTER_LINEAR)`
(`CvResize.cs`), `cv2.Canny` and `cv2.dilate` (`EdgeDetector.cs`).
Copyright the OpenCV authors. https://github.com/opencv/opencv,
license: https://www.apache.org/licenses/LICENSE-2.0

## FFmpeg libswscale (GNU LGPL 2.1 or later), ShotDetector.FastYuv only

Ported: the yuv420p → BGR24 converter (`src/ShotDetector.FastYuv/SwscaleYuv420.cs`) from
`libswscale/yuv2rgb.c` and `libswscale/x86/yuv_2_rgb.asm`. The package is therefore licensed
LGPL-2.1-or-later (`src/ShotDetector.FastYuv/COPYING.LGPL`).
Copyright (C) 2001-2007 Michael Niedermayer, (C) 2009-2010 Konstantin Shishkov.
https://ffmpeg.org, license: https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html

## FFmpeg libraries (GNU LGPL 2.1 or later), ShotDetector.Native.<rid> and the shotdetect CLI

libavcodec, libavformat, libavutil, libswscale, libswresample and libavfilter (yadif and fps only) from FFmpeg 8.1.3
(https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz), built with `tools/native/build-ffmpeg.sh` for decoding only:
no encoders, muxers, devices, network protocols or GPL parts; zlib and dav1d (below) linked in. Each package and
release archive has the exact configure line (`configure.txt`) and the licence (`COPYING.LGPL`). The libraries
are loaded dynamically, so they can be replaced with another build of the same FFmpeg version.
https://ffmpeg.org/legal.html

## dav1d (BSD 2-Clause), ShotDetector.Native.<rid> and the shotdetect CLI

dav1d 1.5.1 (https://code.videolan.org/videolan/dav1d), linked into libavcodec as FFmpeg's AV1 decoder.

```
Copyright © 2018-2019, VideoLAN and dav1d authors
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## zlib (zlib licence), ShotDetector.Native.<rid> and the shotdetect CLI

zlib 1.3.1 (https://zlib.net), linked into FFmpeg's libraries. Copyright (C) 1995-2024 Jean-loup Gailly and Mark
Adler. Provided 'as-is', without any express or implied warranty; see https://zlib.net/zlib_license.html.
