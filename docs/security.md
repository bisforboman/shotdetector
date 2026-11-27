# Security: what we do, and how to check it

For a team deciding whether to adopt ShotDetector: what keeps the code, its dependencies and the published packages
safe, each with a place to check it without taking our word for it. How to report a vulnerability, which versions get
fixes and how fast: [SECURITY.md](../SECURITY.md). Using ShotDetector safely on untrusted input (paths, URLs, raw
ffmpeg arguments, in-process decoding): the README's [Security section](../README.md#security). In-process decoding
and the FFmpeg build are [FrameReader](https://github.com/bisforboman/FrameReader)'s, with
[its own page](https://github.com/bisforboman/FrameReader/blob/main/docs/security.md) (the native CVE check is there).

## At a glance

- **OpenSSF Scorecard**: https://scorecard.dev/viewer/?uri=github.com/bisforboman/shotdetector, an independent score
  of the repository's practices (branch protection, pinned dependencies, token permissions, signed releases, code
  review, static analysis, a security policy), refreshed weekly and on each push to main (scorecard.yml).
- **Advisories**: https://github.com/bisforboman/shotdetector/security/advisories, every vulnerability fixed, with its
  CVE and fixed version. They also reach NuGet's audit, so your `dotnet restore` warns about an affected version.
- **Releases**: [the changelog](../CHANGELOG.md) and https://github.com/bisforboman/shotdetector/releases, each with
  its packages, binaries, SBOMs and attestations.

## Verifying a package or binary

Every package on nuget.org and every binary on a GitHub release was built by `release.yml` in this repository from a
tagged commit, and nothing else pushes to nuget.org: publishing uses NuGet's trusted publishing (a key valid for an
hour, issued to that workflow only; no stored API key). To check one you downloaded:

```bash
gh attestation verify ShotDetector.3.30.0.nupkg --repo bisforboman/shotdetector      # built here, from which commit
gh attestation verify ShotDetector.3.30.0.nupkg --repo bisforboman/shotdetector \
  --predicate-type https://spdx.dev/Document/v2.2                                     # its SBOM, signed
gh attestation verify shotdetect-v3.30.0-linux-x64.tar.gz --repo bisforboman/shotdetector   # a binary
```

- **Provenance**: a signed build attestation (Sigstore, through GitHub) for every `.nupkg`, `.snupkg` and binary
  archive, naming the workflow, commit and run.
- **SBOMs** (SPDX): each package's inside it (`_manifest/spdx_2.2/manifest.spdx.json`) and on the GitHub release,
  with a signed attestation. Each binary's archive carries the FFmpeg build's (`sbom.spdx.json`: FFmpeg and what's
  linked into it, with versions, source URLs, SHA-256, licences and CPEs) and its recipe (`configure.txt`).
- **Reproducible from source**: deterministic builds, SourceLink to the exact commit and symbol packages, so a
  debugger steps into the source the package was built from. The FFmpeg build the release bundles is FrameReader.Native's,
  its packages' signatures checked before use (native-package.yml).

## Keeping dependencies safe

- **NuGet packages**: a package with a known vulnerability, direct or transitive, fails every restore and so every
  build, test, pack and release (NuGet's audit, all severities, as errors:
  [decision](decisions/2026-11-06-vulnerable-packages.md)). A weekly run (vulnerable-packages.yml) catches an
  advisory published against what main already ships, and Dependabot opens updates (security ones at once, the rest
  weekly), FrameReader's releases included.
- **FFmpeg and the native libraries**: FrameReader's build, watched there each week for CVEs (NVD) and new releases;
  [SECURITY.md](../SECURITY.md) says how a fix reaches ShotDetector's users.
- **GitHub Actions**: every external action pinned to a commit SHA (Dependabot moves the pins weekly), every
  workflow's token read-only unless a job needs more, and the release job runs only our code and our FFmpeg build:
  the tests, which use a third-party ffmpeg, run in a job without the publishing credentials
  ([decision](decisions/2026-10-16-security-review-fixes.md)).

## Checking our code

- **CodeQL** on every pull request, every push to main and weekly (codeql.yml; findings under Security, Code
  scanning).
- **The .NET SDK's security analyzers** (CA2xxx, CA3xxx, CA5xxx) as errors on every build
  ([decision](decisions/2026-11-06-security-analyzers.md)).
- **Review**: main is protected; every change goes through a pull request and CI, which compares the results with
  scenedetect's on every change.
- **Security reviews** of the code, the native build and the workflows, with their findings and fixes:
  [2026-10-16](decisions/2026-10-16-security-review-fixes.md).
- **No shell**: ffmpeg and ffprobe start with an argument list, and FFmpeg's pseudo-protocols are refused in paths
  (the README's Security section).
