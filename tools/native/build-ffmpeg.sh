#!/bin/sh
# Builds FFmpeg's shared libraries for ShotDetector.Native.<rid>: decoding only (libavcodec decoders, libavformat
# demuxers, libswscale, libavutil; libswresample too, which FFmpeg.AutoGen loads with libavcodec), LGPL, no
# external libraries. Run on the platform itself, except win-x64, which
# cross-compiles from Linux with mingw-w64.
#
#   tools/native/build-ffmpeg.sh <rid> <output dir>
#
# The output dir gets the four libraries under the names FFmpeg.AutoGen loads (avcodec-62.dll, libavcodec.so.62,
# libavcodec.62.dylib, ...), plus the configure line used (configure.txt) for the LGPL notice.
set -eu

VERSION=8.1.3
SHA256=7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3
rid=$1
out=$(mkdir -p "$2" && cd "$2" && pwd)
work=${TMPDIR:-/tmp}/ffmpeg-build-$rid
rm -rf "$work" && mkdir -p "$work" && cd "$work"

curl -fsSL -o ffmpeg.tar.xz "https://ffmpeg.org/releases/ffmpeg-$VERSION.tar.xz"
echo "$SHA256  ffmpeg.tar.xz" | sha256sum -c - 2>/dev/null || echo "$SHA256  ffmpeg.tar.xz" | shasum -a 256 -c -
tar -xJf ffmpeg.tar.xz
cd "ffmpeg-$VERSION"

# Decoding only: what OpenCV's FFmpeg does for scenedetect, minus everything that writes or filters. No
# --enable-gpl/--enable-nonfree, and --disable-autodetect keeps system libraries (and their licences) out.
set -- --prefix="$work/install" --enable-shared --disable-static --enable-pic \
  --disable-programs --disable-doc --disable-avdevice --disable-avfilter \
  --disable-network --disable-encoders --disable-muxers --disable-autodetect \
  --disable-debug --enable-stripping --enable-optimizations

case "$rid" in
  win-x64) set -- "$@" --arch=x86_64 --target-os=mingw32 --cross-prefix=x86_64-w64-mingw32- \
             --extra-ldflags=-static-libgcc --enable-w32threads ;;
  linux-*) set -- "$@" --enable-pthreads --extra-ldflags='-Wl,-rpath,$$ORIGIN' ;;
  osx-*)   set -- "$@" --enable-pthreads --install-name-dir=@rpath --extra-ldflags=-Wl,-rpath,@loader_path ;;
  *) echo "unknown rid $rid" >&2; exit 1 ;;
esac

./configure "$@" > configure.log || { tail -40 configure.log; tail -40 ffbuild/config.log; exit 1; }
make -j"$(getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.ncpu)" > make.log 2>&1 || { tail -60 make.log; exit 1; }
make install > /dev/null

for lib in avutil swresample swscale avcodec avformat; do
  case "$rid" in
    win-x64) cp "$work"/install/bin/$lib-*.dll "$out/" ;;
    linux-*) f=$(ls "$work"/install/lib/lib$lib.so.* | grep -E "lib$lib\.so\.[0-9]+$"); cp -L "$f" "$out/" ;;
    osx-*)   f=$(ls "$work"/install/lib/lib$lib.*.dylib | grep -E "lib$lib\.[0-9]+\.dylib$"); cp -L "$f" "$out/" ;;
  esac
done
{ echo "FFmpeg $VERSION (https://ffmpeg.org/releases/ffmpeg-$VERSION.tar.xz, sha256 $SHA256)"; echo "./configure $*"; } > "$out/configure.txt"
ls -l "$out"
