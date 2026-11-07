# ShotDetector takes the FrameReader package (2026-11-07)

### Question

With FrameReader in its own repository (docs/framereader-split.md, step 6), ShotDetector takes it as a NuGet package
instead of a ProjectReference. Two choices came up when the work started:

1. What range does ShotDetector's package declare on FrameReader? As a ProjectReference it was `[X.Y.Z, X+1.0.0)`
   (Directory.Build.targets), so a later FrameReader major couldn't replace the one ShotDetector was built against. A
   PackageReference declares what Directory.Packages.props says. Choices: keep the range, `[2.25.0, 3.0.0)`
   (recommended); the minimum only, `>= 2.25.0`, as Dependabot handles any other package.
2. Where do CI and the release get our FFmpeg libraries, which native.yml built here? Choices: from the
   `FrameReader.Native.<rid>` packages on nuget.org, at the FrameReader version in use, as the same native-<rid>
   artifacts (recommended); keep building them here too.

### Answer

The owner, 2026-11-07:

1. **Keep the range.** Directory.Packages.props has `Version="[2.25.0, 3.0.0)"`, and ShotDetector's package declares
   the same. Whether Dependabot still opens FrameReader's minor bumps against a range is to be seen at FrameReader's
   next release. If it doesn't, the fallback is the minimum only.
2. **From the packages.** native-package.yml downloads each FrameReader.Native.<rid> package at the range's lower
   bound, verifies its nuget.org signature, and hands the libraries, configure.txt, the SBOM and the licences on as
   native-<rid>. The jobs that used native.yml's artifacts are unchanged. native.yml and tools/native left with
   FrameReader. The CLI bundles exactly what FrameReader.Native ships.
