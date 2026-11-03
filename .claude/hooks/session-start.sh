#!/bin/bash
# Cloud sessions only: the .NET 10 SDK, the local tool (bro), the restored solution, the pre-commit hook, and BtbN's
# FFmpeg 8.1 gpl-shared build (the release and SHA-256 pinned in .github/actions/btbn-ffmpeg), unpacked once into
# ~/.cache/shotdetector, its bin folder (executables and libraries) first on PATH and as SHOTDETECTOR_FFMPEG_LIBS.
# Idempotent: the container is cached after it runs, so a later start finds everything in place and only checks.
# An optional step that fails says so and the session starts anyway.
set -uo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "${CLAUDE_PROJECT_DIR:-$(dirname "$0")/../..}" || exit 0
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 NUGET_CERT_REVOCATION_MODE=offline
env_lines=("export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 NUGET_CERT_REVOCATION_MODE=offline")

# Ubuntu's own package: the cloud network policy blocks builds.dotnet.microsoft.com (dotnet-install.sh) but not the
# Ubuntu archive.
if ! dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then
  { apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-10.0; } \
    || echo "session-start: dotnet-sdk-10.0 didn't install."
fi

dotnet tool restore > /dev/null || echo "session-start: dotnet tool restore failed (bro format won't run)."
dotnet restore ShotDetector.slnx -v q > /dev/null || echo "session-start: dotnet restore ShotDetector.slnx failed."
git config core.hooksPath .githooks

# FFmpeg 8.1 (Ubuntu's ffmpeg is 6.1, which lacks in_chroma_loc): the pin is read from the action, so it moves with CI's.
action=.github/actions/btbn-ffmpeg/action.yml
release=$(sed -n 's/^ *release=\([^ ]*\)$/\1/p' "$action")
tag=$(sed -n 's/^ *8\.1) tag=\([^ ]*\) ;;$/\1/p' "$action")
name="ffmpeg-$tag-linux64-gpl-shared-8.1"
sum=$(sed -n "s/^ *$name) sum=\([0-9a-f]*\) ;;\$/\1/p" "$action")
dir="$HOME/.cache/shotdetector/btbn-$name"
bin="$dir/$name/bin"
if [ -z "$release" ] || [ -z "$tag" ] || [ -z "$sum" ]; then
  echo "session-start: no FFmpeg 8.1 pin found in $action; set SHOTDETECTOR_FFMPEG_LIBS by hand (CLAUDE.md)."
elif [ ! -x "$bin/ffmpeg" ]; then
  archive="$HOME/.cache/shotdetector/$name.tar.xz"
  mkdir -p "$HOME/.cache/shotdetector"
  if curl -fsSL --retry 3 --connect-timeout 30 -o "$archive" \
      "https://github.com/BtbN/FFmpeg-Builds/releases/download/$release/$name.tar.xz" \
    && [ "$(sha256sum < "$archive" | cut -d' ' -f1)" = "$sum" ]; then
    rm -rf "$dir" && mkdir -p "$dir" && tar -xJf "$archive" -C "$dir" && cp -a "$dir/$name"/lib/*.so* "$bin/" \
      || { echo "session-start: unpacking $name failed."; rm -rf "$dir"; }
  else
    echo "session-start: $name didn't download or its SHA-256 isn't the pinned one; the in-process tests won't check."
  fi
  rm -f "$archive"
fi
if [ -x "$bin/ffmpeg" ]; then
  export PATH="$bin:$PATH"
  env_lines+=("export PATH=\"$bin:\$PATH\"" "export SHOTDETECTOR_FFMPEG_LIBS=\"$bin\"")
  echo "session-start: $(ffmpeg -hide_banner -version | head -1) on PATH and as SHOTDETECTOR_FFMPEG_LIBS."
fi

if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  printf '%s\n' "${env_lines[@]}" >> "$CLAUDE_ENV_FILE"
fi

command -v pwsh > /dev/null || echo "session-start: no pwsh (make-samples.ps1, mutations): dotnet tool install -g PowerShell."
python3 -c 'import scenedetect' 2> /dev/null \
  || echo "session-start: for tools/compare.py: pip install scenedetect==0.7.2 opencv-python-headless (~1 min)."
exit 0
