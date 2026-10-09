#!/bin/sh
# Builds FFmpeg's shared libraries for ShotDetector.Native.<rid>: decoding only (libavcodec decoders, libavformat
# demuxers, libswscale, libavutil; libswresample too, which FFmpeg.AutoGen loads with libavcodec), plus libavfilter
# with only yadif (deinterlacing), fps (FrameReader's FrameRate), aspectralstats (its SpectralStats) and a graph's
# buffer/buffersink and abuffer/abuffersink ends. LGPL; linked in statically: zlib (PNG, compressed
# MOV/MKV headers) and dav1d (AV1, BSD-2-Clause). Run on the platform itself, except win-x64, which cross-compiles from
# Linux with mingw-w64. Needs nasm (x86), meson, ninja and pkg-config.
#
#   tools/native/build-ffmpeg.sh <rid> <output dir>
#
# The output dir gets the four libraries under the names FFmpeg.AutoGen loads (avcodec-62.dll, libavcodec.so.62,
# libavcodec.62.dylib, ...), plus the configure line used (configure.txt) for the LGPL notice.
set -eu

VERSION=8.1.3
SHA256=7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3
ZLIB=1.3.1
ZLIB_SHA256=9a93b2b7dfdac77ceba5a558a580e74667dd6fede4585b91eefb60f03b72df23
DAV1D=1.5.1
DAV1D_SHA256=401813f1f89fa8fd4295805aa5284d9aed9bc7fc1fdbe554af4292f64cbabe21
rid=$1
out=$(mkdir -p "$2" && cd "$2" && pwd)
work=${TMPDIR:-/tmp}/ffmpeg-build-$rid
deps=$work/deps
rm -rf "$work" && mkdir -p "$work" "$deps/lib" "$deps/include" && cd "$work"
jobs=$(getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.ncpu)

fetch() { # url file sha256
  curl -fsSL -o "$2" "$1"
  echo "$3  $2" | sha256sum -c - 2>/dev/null || echo "$3  $2" | shasum -a 256 -c -
}
fetch "https://ffmpeg.org/releases/ffmpeg-$VERSION.tar.xz" ffmpeg.tar.xz "$SHA256"
fetch "https://zlib.net/fossils/zlib-$ZLIB.tar.gz" zlib.tar.gz "$ZLIB_SHA256"
fetch "https://downloads.videolan.org/pub/videolan/dav1d/$DAV1D/dav1d-$DAV1D.tar.xz" dav1d.tar.xz "$DAV1D_SHA256"
tar -xJf ffmpeg.tar.xz && tar -xzf zlib.tar.gz && tar -xJf dav1d.tar.xz

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

cd "ffmpeg-$VERSION"
# Only our static zlib and dav1d, never the system's.
export PKG_CONFIG_LIBDIR="$deps/lib/pkgconfig"

# Decoding only: what OpenCV's FFmpeg does for scenedetect, minus everything that writes, plus yadif for
# DeinterlaceMode (LGPL, like the rest). No --enable-gpl/--enable-nonfree, and --disable-autodetect keeps system
# libraries (and their licences) out: zlib and dav1d are the ones built above, linked in.
set -- --prefix="$work/install" --enable-shared --disable-static --enable-pic \
  --disable-programs --disable-doc --disable-avdevice --disable-filters --enable-filter=buffer,buffersink,abuffer,abuffersink,yadif,fps,aspectralstats \
  --disable-network --disable-encoders --disable-muxers --disable-autodetect \
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
  echo "./configure $*"
} > "$out/configure.txt"
ls -l "$out"
