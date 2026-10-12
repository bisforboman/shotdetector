### New

- FrameReader: `EncryptionOptions.WidevinePsshData`, the Widevine pssh data as a DRM provider supplies it (with its
  provider, content ID and so on), written into the pssh box unchanged, the box
  `pssh-box.py --widevine-system-id --pssh-data` gives (issue #185). Null: built from the key ID as before.
