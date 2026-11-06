# Vulnerable NuGet packages (2026-11-06)

Until now NuGet's audit only warned: a package with a known advisory, direct or transitive, could restore, build, pack
and ship (the CLI's binaries, the packages' dependencies) without anything stopping it, and an advisory published
after a package was restored showed nowhere until someone read a build log.

## Question

What should happen when a package this repository builds or ships has a known vulnerability?

## Choices

1. **Every build fails, and a weekly scan of main opens an issue** (taken): Directory.Build.props sets `NuGetAudit`
   true, `NuGetAuditMode` all (transitive packages too), `NuGetAuditLevel` low and NU1901 to NU1904 (low, moderate,
   high, critical) as errors, appended to `WarningsAsErrors`; every restore fails, locally, in CI, at pack and at
   release. NU1905 (an audit source that can't be reached) stays a warning, so an outage of the feed's vulnerability
   data doesn't stop work. `.github/workflows/vulnerable-packages.yml` restores every solution and project weekly with
   those errors off, lists the vulnerable packages (`tools/vulnerable-packages.py`), and on a finding fails and opens
   an issue, or comments on the open one (`tools/nightly-issue.sh`: one issue per streak), naming package, version,
   severity, advisory and the projects. Not a required check.
2. Fail only CI's builds (a property set when `GITHUB_ACTIONS` is true), warnings locally.
3. Keep the warnings and rely on Dependabot alerts.

## Answer

1. A finding is fixed by moving the package (in Directory.Packages.props, with a comment naming the advisory; a
   transitive one through a direct reference at a fixed version), never by suppressing the warning
   (`NuGetAuditSuppress`, `NoWarn`). None was vulnerable when this went in.

The owner also turns on Dependabot alerts and Dependabot security updates (Settings, Code security), so an advisory
gets a pull request without waiting for the weekly scan.

The audit also covers the packages the SDK adds itself, such as Native AOT's `Microsoft.DotNet.ILCompiler`, whose
version is the SDK's runtime. Those are fixed by moving the SDK: on this PR, the Alpine image pinned by digest (ci.yml's
AOT matrix and `alpine` job, release.yml's AOT matrix) still had .NET 10.0.13, whose ILCompiler has
GHSA-p3jp-9v83-76w9, so its AOT restore failed; the image moved to 10.0-alpine with 10.0.14. The other jobs take the
newest 10.0.x through setup-dotnet. Without the gate, release.yml would have built the musl binaries with it.
