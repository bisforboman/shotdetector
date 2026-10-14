# Widevine pssh data from the caller (2026-10-12, issue #185)

#185's DRM provider supplies the WidevinePsshData itself. Asked how EncryptionOptions should take it: **the data
as bytes** (`WidevinePsshData`, wrapped in the version 0 Widevine box, checked against `pssh-box.py --pssh-data`), over
the whole box (Shaka's `--pssh`) or a general list of pssh boxes for any system (PlayReady later).
