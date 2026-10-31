# The Jellyfin plugin's repository and first scope (2026-10-31)

The owner chose to build the Jellyfin plugin scoped in issue #634 (docs/ideas/jellyfin-plugin.md). It lives in a repository
of its own, because Jellyfin requires GPL-3.0 plugins (the note's section 4) and it releases on Jellyfin's schedule, not
on NuGet. ShotDetector, FrameReader and FastYuv keep their licences; the plugin uses them as packages. Three questions:
the note's questions 1-3, the first of them narrowed to the repository's name.

## The repository's name

### Choices

1. **jellyfin-plugin-shotchapters**: Jellyfin's `jellyfin-plugin-<name>` convention, named after what it does.
2. jellyfin-plugin-shotdetector: named after the library, which would fit if it grows beyond chapters.

### Answer

**1** (the owner, 2026-10-31): `bisforboman/jellyfin-plugin-shotchapters`.

## Visibility

### Choices

1. **Public from the start**: the code is GPL-3.0 anyway, and users install plugins from a public manifest.
2. Private until the first slice works on a real server.

### Answer

**1** (the owner, 2026-10-31).

## Jellyfin versions and the first version's features

### Choices

1. Jellyfin 12, chapters only: the first version is one PR, and segments come later.
2. **Jellyfin 12, chapters and black/fade segments**: an `IMediaSegmentProvider` for fades as well, about 1-2 more PRs
   before the first release.
3. Jellyfin 10.10/10.11 too: ShotDetector and FrameReader would have to build for net8.0/net9.0 first.

### Answer

**2** (the owner, 2026-10-31). The plugin targets Jellyfin 12 (net10.0), so no new target framework is needed here.

For the segments, the black/fade spans have to come back as time ranges: ThresholdDetector reports cuts, not spans
(the note's section 3, gap 3). Whether those spans become a ShotDetector API or are worked out in the plugin from
`average_rgb` is still open (the note's question 4).
