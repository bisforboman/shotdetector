#!/bin/bash
# The release archive of a published shotdetect binary: release.yml's `binaries` job and ci.yml's `aot` job, so CI
# builds the archive the release uploads. Run from the repository root after
#   dotnet publish src/ShotDetector.Cli -c Release -r <rid> -p:PublishAot=true ... -o out
# with our FFmpeg build in native/<rid> (the native-<rid> artifact). Prints the archive's file name.
#   tools/archive-binary.sh <rid> <version>
set -euo pipefail
rid=$1 version=$2
name=shotdetect-v$version-$rid
rm -f out/*.pdb out/*.dbg
cp LICENSE THIRD-PARTY-NOTICES.md src/FrameReader.Native/COPYING.LGPL src/FrameReader.Native/COPYING.dav1d \
  "native/$rid/configure.txt" "native/$rid/sbom.spdx.json" out/
case "$rid" in linux-*) cp src/FrameReader.Native/COPYING.openssl out/ ;; esac
if [ "$rid" = win-x64 ]; then
  rm -f "$name.zip"
  (cd out && 7z a "../$name.zip" . > /dev/null) && echo "$name.zip"
else
  tar -czf "$name.tar.gz" -C out . && echo "$name.tar.gz"
fi
