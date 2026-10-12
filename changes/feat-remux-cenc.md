### New

- FrameReader: `RemuxOptions.Encryption`, Common Encryption (CENC, AES-CTR) of .mp4 and DASH output with a key ID
  and key, the files ffmpeg's `-encryption_scheme cenc-aes-ctr` gives. `Widevine = true` adds, for DASH, Widevine's
  `pssh` box to the init segments (`pssh-box.py`'s) and `ContentProtection` to the MPD, as Shaka Packager writes them
  (issue #171).
