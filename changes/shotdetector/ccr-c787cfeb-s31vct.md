### New

- A security policy ([SECURITY.md](https://github.com/bisforboman/shotdetector/blob/main/SECURITY.md)): which versions
  get fixes, how to report a vulnerability privately, and how fast fixes ship (high and critical within 30 days).
  What protects the packages and how to check it: docs/security.md.
- Every package (`ShotDetector`, `ShotDetector.FastYuv`, `ShotDetector.Cli`) carries an SPDX SBOM
  (`_manifest/spdx_2.2/manifest.spdx.json`), also attached to the GitHub release with a signed attestation
  (`gh attestation verify <nupkg> --repo bisforboman/shotdetector --predicate-type https://spdx.dev/Document/v2.2`).
