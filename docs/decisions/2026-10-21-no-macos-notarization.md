# No macOS notarization (2026-10-21)

### Question

Do you have or plan to get an Apple Developer account?

The backlog's Waiting row "macOS: sign and notarize the osx-arm64 binary, then perhaps a Homebrew tap" waited on it.
release.yml doesn't codesign or notarize the `osx-arm64` release binary, so a copy downloaded in a browser is
quarantined and Gatekeeper blocks it and its FFmpeg libraries until the user clears the quarantine. Notarizing needs an
Apple Developer account (its certificate and credentials as secrets in the `release` environment); a Homebrew tap
would have followed.

### Choices

1. **Yes, I'll set it up**: release.yml signs and notarizes the binary once the secrets are in place.
2. **No, drop it**: the binary stays unsigned; the row is closed as Won't do.
3. **Undecided**: the row keeps waiting.

### Answer

**2. No, drop it.** There is no Apple Developer account. The macOS binary stays unsigned and unnotarized, and README's
"The release binaries" keeps explaining the quarantine and how to clear it (`xattr -dr com.apple.quarantine`; `curl`
downloads aren't quarantined). The backlog row moved to docs/backlog-done.md as Won't do; a Homebrew tap, which came
after notarizing in that row, goes with it.
