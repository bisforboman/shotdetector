# Security policy

ShotDetector reads video, often video someone else made. This is how vulnerabilities in it are reported, fixed and
announced. What protects the code and the packages, and how to check it yourself: [docs/security.md](docs/security.md).

## Supported versions

Security fixes ship as a patch release of the newest minor version (`X.Y.Z+1`); earlier minors don't get backports,
so stay on the newest minor of the major you use (a minor never breaks the API: package validation fails a release
that would). `ShotDetector`, `ShotDetector.FastYuv`, `ShotDetector.Cli` and the `shotdetect` binaries on the GitHub
releases share one version.

| Version | Fixes |
| --- | --- |
| The newest minor (see [the changelog](CHANGELOG.md)) | Yes |
| Older minors and majors | No: move to the newest |

## Reporting a vulnerability

Please don't open a public issue. Report it privately through GitHub: the repository's **Security** tab, **Report a
vulnerability** ([direct link](https://github.com/bisforboman/shotdetector/security/advisories/new)). Say which package
or binary and version, how to reproduce it (a file that triggers it helps), and what an attacker gains.

In-process decoding is [FrameReader](https://github.com/bisforboman/FrameReader)'s, and the FFmpeg build in
FrameReader.Native and in the `shotdetect` binaries is FrameReader's too: a vulnerability there goes to
[FrameReader's security policy](https://github.com/bisforboman/FrameReader/security/policy) (the same maintainer and
times; reported here, it's moved there). A bug in FFmpeg itself also goes to FFmpeg (https://ffmpeg.org/security.html).

## What happens then

- **Within 7 days**: an answer that the report arrived, and a first assessment.
- **Within 30 days** for a high or critical vulnerability (CVSS 7.0 or more), **90 days** for the others: a release
  with the fix. A vulnerability in FFmpeg or a library linked into it counts from the day upstream has a fix.
- **With the release**: a GitHub security advisory with a CVE (GitHub assigns one), the fixed version and credit to
  the reporter unless they'd rather not. GitHub's advisory database feeds NuGet's audit, so `dotnet restore` warns
  every project that still uses an affected version. The CHANGELOG entry names the CVE.

Until the advisory is published, the report stays between the reporter and the maintainer.

## Fixes in FrameReader and FFmpeg

- **The libraries**: `ShotDetector` depends on `FrameReader` within a major version (`[X.Y.Z, next major)`), so a
  FrameReader fix reaches you by updating FrameReader (and FrameReader.Native, which you reference yourself), without
  waiting for a ShotDetector release. NuGet's audit tells you when to.
- **`ShotDetector.Cli` and the `shotdetect` binaries** bundle FrameReader.Native's FFmpeg build, so a fix there ships
  as a ShotDetector patch release too, within the same times.
- **The ffmpeg executable** (`Decoder = VideoDecoder.FfmpegProcess`, URLs, the image and clip exports) is yours:
  keep it current.
