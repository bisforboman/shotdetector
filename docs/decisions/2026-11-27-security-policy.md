# A security policy, and showing adopters how security is handled (2026-11-27)

A company adopting ShotDetector and FrameReader will ask whether they're secure and whether security updates are
managed. Most of the controls were in place (NuGet audit as errors, CodeQL, the security analyzers, build attestations,
trusted publishing, pinned actions, the native build's SBOM); missing were a written policy, a CVE check for the native
libraries, SBOMs for the managed packages and public signals an outsider can check. Decided for both repositories at
once; FrameReader's copy is its docs/decisions/2026-11-27-security-policy.md.

## Questions

Asked with the multiple-choice prompt:

1. "Which versions get security fixes?" Options: "Latest minor only (Recommended)", "Latest minor + previous major"
   (backports to the last minor of the previous major for a while after a new major), "Latest two minors".
2. "What response times should SECURITY.md commit to for privately reported vulnerabilities?" Options: "Ack 7d, fix
   30d (Recommended)" (90 days for below high), "Ack 3d, fix 14d", "Best effort, no numbers".
3. "For a CVE in the native libraries FrameReader.Native ships, what release commitment?" Options: "Same as reported
   vulns (Recommended)" (counted from upstream's fix), "Within 7 days of upstream fix", "No specific number".

## Answers

1. **The newest minor only.** Semantic versioning with package validation means moving to the newest minor never
   breaks the API, so a backport line buys little for a single maintainer's time.
2. **Acknowledge within 7 days; a fix within 30 days for high and critical (CVSS 7.0 on), 90 for the others.**
   Reports go through GitHub's private vulnerability reporting; fixes are published as GitHub security advisories with
   a CVE, which reach NuGet's audit, so adopters' restores warn about affected versions.
3. **The same times, counted from the day upstream has a fix** (a release or an applicable patch).

With them (proposed together, no further choice): `SECURITY.md`; [docs/security.md](../security.md), each control with
a place to check it; in FrameReader, `native-cves.yml` (weekly: the NVD's CVEs for the exact versions build-ffmpeg.sh pins, matched
locally against each CVE's version ranges, an issue per new one, at most 10 a run; the backlog's row); an SPDX SBOM in
each package (Microsoft.Sbom.Targets: FrameReader; here ShotDetector, ShotDetector.FastYuv and ShotDetector.Cli)
attached to each GitHub release with a signed attestation, beside the native build's SBOMs; and the OpenSSF Scorecard (scorecard.yml, its badge in the README).

Left to the owner, as repository and nuget.org settings: private vulnerability reporting (Settings, Code security;
SECURITY.md relies on it), secret scanning with push protection, and a reserved ID prefix on nuget.org (`FrameReader.*`
and `ShotDetector.*`, by mail to account@nuget.org) so the packages show as from a verified owner. Author-signed packages stay out
([2026-10-06](2026-10-06-issues-16-19-deinterlacing-exceptions-supply-chain-tracing.md): attestations instead of a paid certificate); nuget.org's repository signature
remains.
