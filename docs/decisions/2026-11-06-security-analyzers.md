# Security analyzers on every build, as errors (2026-11-06)

The vulnerable-package gate ([2026-11-06-vulnerable-packages.md](2026-11-06-vulnerable-packages.md)) keeps packages
with known advisories out, but nothing in the build looked at this repository's own code for security bugs. CodeQL
(codeql.yml) already scans it, as code scanning, not in the build. The code reads untrusted video and starts ffmpeg
and ffprobe, and FrameReader loads native libraries.

## Question

Asked with the multiple-choice prompt after the two questions in 2026-11-06-vulnerable-packages.md, for the owner's
five repositories: "Should I also add scanning of your own code?" Options:

1. "Security analyzers, all 5 (Recommended)": the .NET SDK's Security rules (CA2xxx, CA3xxx, CA5xxx), all of them,
   as errors, in all five repositories.
2. "CodeQL on StanzaSharp, stylebro" (public repositories: free).
3. "CodeQL on Svarta" (private: needs paid GitHub Code Security).
4. "Not now".

## Answer

**The security analyzers in all five repositories, and CodeQL on StanzaSharp and stylebro.** ShotDetector already has
CodeQL, which stays as it is; this adds the analyzers, as ManagedCodecs did:

- Directory.Build.props sets `AnalysisModeSecurity` to `All`: the SDK adds its global config enabling every Security
  rule. It also sets the SDK's private property
  `_GlobalAnalyzerConfigFileName_MicrosoftCodeAnalysisNetAnalyzersSecurity_WarnAsErrorSuffix` to `_warnaserror`,
  which picks that config's variant with every rule at error: what `CodeAnalysisTreatWarningsAsErrors` would pick, but
  for Security only (that property would also make other categories' default rules errors). Every build fails on a
  finding, local, CI, pack and release, not only those with `-warnaserror`.
- `dotnet_analyzer_diagnostic.category-Security.severity = error` in .editorconfig doesn't work (Svärta tried it
  first): the SDK's global config sets each rule's severity by id, and a rule's own severity wins over its category's.
- Every project here is net10.0, where the SDK turns its analyzers on; `EnableNETAnalyzers` and
  `AnalysisLevelSecurity` (`latest`) keep the Security rules on in a project that ever targets something older.
- Since the suffix is the SDK's, not a documented property, the target `CheckSecurityAnalyzers`
  (Directory.Build.targets) fails the build unless the config in use exists and ends in
  `_all_warnaserror.globalconfig`. It runs after the SDK's target that picks the config, or after `CoreCompile` where
  that target isn't imported, so a project with the analyzers off fails too. Checked: a temporary `MD5.HashData` call
  in src/ShotDetector failed its plain build with CA5351; clearing the suffix (`-p:..._WarnAsErrorSuffix=`) and
  `-p:EnableNETAnalyzers=false` each fail with the target's message.
- Everything CI builds goes through MSBuild with this props file: ShotDetector.slnx (src, tests, ClipMaker,
  FramesWithoutFfmpeg, AudioWithoutFfmpeg, examples, benchmarks/micro, the wasm bench's native twin),
  benchmarks/FrameReader.Benchmarks, tools/reach, the WebAssembly bench (benchmarks/wasm/Wasm, wasm-tools workload),
  the CLI with `-p:WithFastYuv=true` and its Native AOT publish. All built clean once the findings below were handled.
- A real weakness is fixed, the owner deciding how; a false positive is suppressed where it is, as narrowly as
  possible (`[SuppressMessage]` with a justification, or one path-scoped .editorconfig section), never by a rule off
  for the whole repository.

## Findings when it was turned on

Two rules, neither a weakness, both in tests:

- **CA5394 (insecure randomness)** in 20 test files: 14 in tests/ShotDetector.Tests, 5 in tests/FrameReader.Tests
  (AudioWriterTests, CancellationTests, FrameDecoderTests, LoudnessMeterTests, VideoWriterTests) and
  tests/FramesWithoutFfmpeg: a seeded `System.Random` makes test frames and samples, reproducible on purpose.
  Suppressed for `tests/**.cs` in .editorconfig.
- **CA5392 (P/Invoke without DefaultDllImportSearchPaths)**: LoudnessMeterTests' `exp2` from `libm.so.6`, the C
  library's result the meter's is compared with. Fixed with `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`:
  the loader no longer looks next to the test assembly first, where there is no libm; the system's libm loads as before.

Nothing in src/ was reported. The places one would look at by hand are not patterns these rules flag, and are as
intended: ffmpeg and ffprobe start from a `ProcessStartInfo` with an argument list (no shell, no command line to
inject into; the input path follows `-i`), from the folder the caller gives or PATH; FFmpegLibraries.Load takes the
libraries from the caller's folder, else next to the app, else the system's places (on Windows PATH's folders by
full path, never the current folder).

## Consequences

- An SDK with new Security rules may fail the build after an update; its findings are triaged like these.
- The analyzers find patterns (weak cryptography, unsafe deserialization, XML, P/Invoke and process handling), not
  the bugs a decoder's caller is most likely to hit (sizes from a file, malformed streams): those stay with the
  broken-input tests, the real-world runs and CodeQL.
