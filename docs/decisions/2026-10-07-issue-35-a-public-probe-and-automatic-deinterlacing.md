# Issue #35: a public probe and automatic deinterlacing (2026-10-07)

### Question

A worker probes each file with ffprobe for the field order (to set Deinterlace), then ShotDetector probes it again.
It asks for a public probe result (field order included) that detection could take back, an Auto deinterlace mode,
and possibly audio and container details. What fits, and when?

### Choices

- Scope: Auto deinterlace + a header-only public probe (no pass-back, no audio); Auto deinterlace only; everything
  asked; a reply only.
- Timing: 1.1 after 1.0 (keeps the quiet period); now as 0.8 (restarts it).
- Reply: draft for approval; post when it ships.

### Answer

**Auto deinterlace + a public probe, now as 0.8, reply when it ships.** Passing the probe back saves little: the
costly part of detection's probe is reading every packet's timestamp, which a header probe doesn't do. Deinterlace
became an enum (Off/On/Auto), a breaking change before 1.0; the two quiet weeks before 1.0 restart with 0.8.0.
