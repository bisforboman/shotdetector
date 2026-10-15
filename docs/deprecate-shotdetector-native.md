# Deprecating ShotDetector.Native on nuget.org

ShotDetector 3.0 stopped publishing the `ShotDetector.Native` packages: they were aliases of `FrameReader.Native` (the
same FFmpeg libraries). Their 2.x versions stay on nuget.org; deprecating them makes NuGet warn whoever still references
one and suggest the replacement. It doesn't remove or unlist anything, and can be undone. Done by the packages' owner on
nuget.org (backlog section 6).

## For each package

1. Open the package's page (`https://www.nuget.org/packages/<id>`) and click **Manage package** in the right-hand
   column.
2. Expand **Deprecation**.
3. Under **Select versions**, tick all of them (the meta package has 3, each platform package 25: 0.7.0 to 2.2.1).
4. Reason: **This package is legacy and is no longer maintained**.
5. **Alternate package**: the one in the table below, version **Latest**.
6. Custom message (optional): *Replaced by FrameReader.Native (the same FFmpeg libraries). ShotDetector 3.0 no longer
   publishes ShotDetector.Native.*
7. **Save**.

| Package | Alternate package |
| --- | --- |
| ShotDetector.Native | FrameReader.Native |
| ShotDetector.Native.win-x64 | FrameReader.Native.win-x64 |
| ShotDetector.Native.linux-x64 | FrameReader.Native.linux-x64 |
| ShotDetector.Native.linux-arm64 | FrameReader.Native.linux-arm64 |
| ShotDetector.Native.linux-musl-x64 | FrameReader.Native.linux-musl-x64 |
| ShotDetector.Native.osx-arm64 | FrameReader.Native.osx-arm64 |

## Afterwards

The registration API shows it: `https://api.nuget.org/v3/registration5-gz-semver2/shotdetector.native.linux-x64/index.json`
lists a `deprecation` on each version. Then the backlog row is done.
