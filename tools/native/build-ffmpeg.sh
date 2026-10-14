#!/bin/sh
# Builds FFmpeg's shared libraries for FrameReader.Native.<rid>: decoding (libavcodec decoders, libavformat
# demuxers), plus the AAC, MP3 (LAME) and 16-bit PCM encoders and the muxers FrameReader's AudioWriter and Remux
# write with (dash for fMP4 segments and an MPD, issue #171), libswscale, libavutil; libswresample too, which FFmpeg.AutoGen loads with libavcodec), plus libavfilter
# with only yadif (deinterlacing), fps (FrameReader's FrameRate), aspectralstats (its SpectralStats), the audio
# filters its AudioFilter offers (EQ and level, dynamics, loudness, detection; aformat/aresample to convert) and a
# graph's buffer/buffersink and abuffer/abuffersink ends. LGPL; linked in statically: zlib (PNG, compressed
# MOV/MKV headers), dav1d (AV1, BSD-2-Clause) and LAME (MP3, LGPL). Run on the platform itself, except win-x64, which cross-compiles from
# Linux with mingw-w64. Needs nasm (x86), meson, ninja and pkg-config.
#
#   tools/native/build-ffmpeg.sh <rid> <output dir>
#
# The output dir gets the four libraries under the names FFmpeg.AutoGen loads (avcodec-62.dll, libavcodec.so.62,
# libavcodec.62.dylib, ...), plus the configure line used (configure.txt) for the LGPL notice.
set -eu

VERSION=8.1.3
SHA256=7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3
ZLIB=1.3.2
ZLIB_SHA256=bb329a0a2cd0274d05519d61c667c062e06990d72e125ee2dfa8de64f0119d16
DAV1D=1.5.4
DAV1D_SHA256=686616b7c69eb88d44459391ab25cac13b6647a3b288835c5784e71c1514a5c5
LAME=3.100
LAME_SHA256=ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e
rid=$1
out=$(mkdir -p "$2" && cd "$2" && pwd)
work=${TMPDIR:-/tmp}/ffmpeg-build-$rid
deps=$work/deps
rm -rf "$work" && mkdir -p "$work" "$deps/lib" "$deps/include" && cd "$work"
jobs=$(getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.ncpu)

# file sha256 url...: the first source whose download has that sha256. Second sources serve the same archives (checked
# by hash), so a site that's down (downloads.videolan.org once held up a release) doesn't stop the build.
fetch() {
  file=$1 sum=$2
  shift 2
  for url in "$@"; do
    if curl -fsSL --retry 3 --retry-all-errors --retry-delay 10 --connect-timeout 30 -o "$file" "$url" &&
      { echo "$sum  $file" | sha256sum -c - >/dev/null 2>&1 || echo "$sum  $file" | shasum -a 256 -c - >/dev/null 2>&1; }; then
      return 0
    fi
    echo "fetch: $url failed or didn't match its sha256" >&2
  done
  echo "fetch: no source gave $file" >&2
  return 1
}
fetch ffmpeg.tar.xz "$SHA256" "https://ffmpeg.org/releases/ffmpeg-$VERSION.tar.xz"
fetch zlib.tar.gz "$ZLIB_SHA256" "https://zlib.net/fossils/zlib-$ZLIB.tar.gz" \
  "https://github.com/madler/zlib/releases/download/v$ZLIB/zlib-$ZLIB.tar.gz"
fetch dav1d.tar.xz "$DAV1D_SHA256" "https://downloads.videolan.org/pub/videolan/dav1d/$DAV1D/dav1d-$DAV1D.tar.xz" \
  "https://download.videolan.org/pub/videolan/dav1d/$DAV1D/dav1d-$DAV1D.tar.xz"
fetch lame.tar.gz "$LAME_SHA256" "https://downloads.sourceforge.net/project/lame/lame/$LAME/lame-$LAME.tar.gz" \
  "https://deb.debian.org/debian/pool/main/l/lame/lame_$LAME.orig.tar.gz"
tar -xJf ffmpeg.tar.xz && tar -xzf zlib.tar.gz && tar -xJf dav1d.tar.xz && tar -xzf lame.tar.gz

# zlib, static and position-independent (it ends up inside libavcodec/libavformat).
(
  cd "zlib-$ZLIB"
  case "$rid" in
    win-x64) make -f win32/Makefile.gcc PREFIX=x86_64-w64-mingw32- libz.a > make.log
             cp libz.a "$deps/lib/" && cp zlib.h zconf.h "$deps/include/" ;;
    *) CFLAGS="-O2 -fPIC" ./configure --static --prefix="$deps" > configure.log && make -j"$jobs" install > make.log ;;
  esac
)

# dav1d, static: FFmpeg's libdav1d wrapper is its software AV1 decoder (the native av1 one needs a GPU).
(
  cd "dav1d-$DAV1D"
  set -- --buildtype=release --default-library=static -Db_staticpic=true -Denable_tools=false -Denable_tests=false \
    --prefix="$deps" --libdir=lib
  [ "$rid" = win-x64 ] && set -- "$@" --cross-file=package/crossfiles/x86_64-w64-mingw32.meson
  meson setup build "$@" > meson.log || { tail -40 meson.log; exit 1; }
  ninja -C build install > ninja.log || { tail -40 ninja.log; exit 1; }
)

# LAME, static: FrameReader's AudioWriter encodes MP3 with it (LGPL, like FFmpeg; the MP3 patents have expired).
(
  cd "lame-$LAME"
  set -- --prefix="$deps" --enable-static --disable-shared --disable-frontend --disable-decoder --disable-gtktest
  [ "$rid" = win-x64 ] && set -- "$@" --host=x86_64-w64-mingw32
  CFLAGS="-O2 -fPIC" ./configure "$@" > configure.log || { tail -40 configure.log; exit 1; }
  make -j"$jobs" install > make.log 2>&1 || { tail -40 make.log; exit 1; }
)

cd "ffmpeg-$VERSION"
# Only our static zlib and dav1d, never the system's.
export PKG_CONFIG_LIBDIR="$deps/lib/pkgconfig"

# Decoding only: what OpenCV's FFmpeg does for scenedetect, minus everything that writes, plus yadif for
# DeinterlaceMode (LGPL, like the rest); format, null and scale for VideoWriter's graph to the encoder's pixel format
# (scale is the converter FFmpeg inserts; without them every VideoWriter failed, issue #127); FrameReader's TestMedia: the
# sources, concat, setfield/setsar/setparams and trim, FFmpeg's own mpeg2video and mpeg4 encoders, more PCM (issue #237); PNG frames (the png encoder, the image2
# muxer) and MPEG program streams (the mpeg muxer), so ShotDetector's tests make every clip without the executable. No --enable-gpl/--enable-nonfree, and --disable-autodetect keeps system
# libraries (and their licences) out: zlib and dav1d are the ones built above, linked in.
set -- --prefix="$work/install" --enable-shared --disable-static --enable-pic \
  --disable-programs --disable-doc --disable-avdevice --disable-filters --enable-filter=buffer,buffersink,abuffer,abuffersink,yadif,fps,aspectralstats,format,null,scale \
  --enable-filter=volume,equalizer,bass,treble,highpass,lowpass,bandpass,bandreject,afade,pan,acompressor,alimiter,dynaudnorm,agate,ebur128,loudnorm,silencedetect,astats,aformat,aresample \
  --enable-filter=testsrc2,smptebars,mandelbrot,color,sine,anullsrc,concat,setfield,setsar,setparams,trim,atrim \
  --disable-network --disable-encoders --disable-muxers --disable-autodetect \
  --enable-libmp3lame --enable-encoder=aac,libmp3lame,pcm_s16le,pcm_s24le,pcm_s32le,pcm_f32le,mpeg2video,mpeg4,png \
  --enable-muxer=mp4,mov,ipod,avi,matroska,matroska_audio,webm,adts,mp3,wav,flac,ogg,opus,mpegts,mpeg,image2,srt,webvtt,ass,dash \
  --enable-zlib --enable-libdav1d --pkg-config=pkg-config --pkg-config-flags=--static \
  --extra-cflags=-I"$deps/include" --extra-ldflags=-L"$deps/lib" \
  --disable-debug --enable-stripping --enable-optimizations

case "$rid" in
  # D3D11VA/DXVA2 for FrameReader's HardwareDecoding: Windows' own APIs, no extra libraries.
  win-x64) set -- "$@" --enable-d3d11va --enable-dxva2 --arch=x86_64 --target-os=mingw32 --cross-prefix=x86_64-w64-mingw32- \
             --extra-ldflags=-static-libgcc --enable-w32threads ;;
  # No RUNPATH on Linux: the loader loads each library's dependencies first, by full path.
  linux-*) set -- "$@" --enable-pthreads ;;
  osx-*)   set -- "$@" --enable-pthreads --install-name-dir=@rpath --extra-ldflags=-Wl,-rpath,@loader_path ;;
  *) echo "unknown rid $rid" >&2; exit 1 ;;
esac

./configure "$@" > configure.log || { tail -40 configure.log; tail -40 ffbuild/config.log; exit 1; }
make -j"$jobs" > make.log 2>&1 || { tail -60 make.log; exit 1; }
make install > /dev/null

for lib in avutil swresample swscale avcodec avformat avfilter; do
  case "$rid" in
    win-x64) cp "$work"/install/bin/$lib-*.dll "$out/" ;;
    linux-*) f=$(ls "$work"/install/lib/lib$lib.so.* | grep -E "lib$lib\.so\.[0-9]+$"); cp -L "$f" "$out/" ;;
    osx-*)   f=$(ls "$work"/install/lib/lib$lib.*.dylib | grep -E "lib$lib\.[0-9]+\.dylib$"); cp -L "$f" "$out/" ;;
  esac
done
{
  echo "FFmpeg $VERSION (https://ffmpeg.org/releases/ffmpeg-$VERSION.tar.xz, sha256 $SHA256)"
  echo "zlib $ZLIB (https://zlib.net/fossils/zlib-$ZLIB.tar.gz, sha256 $ZLIB_SHA256), static"
  echo "dav1d $DAV1D (https://downloads.videolan.org/pub/videolan/dav1d/$DAV1D/dav1d-$DAV1D.tar.xz, sha256 $DAV1D_SHA256), static"
  echo "LAME $LAME (https://downloads.sourceforge.net/project/lame/lame/$LAME/lame-$LAME.tar.gz, sha256 $LAME_SHA256), static"
  echo "./configure $*"
} > "$out/configure.txt"
ls -l "$out"
