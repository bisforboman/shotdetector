# scenedetect 0.7.2 as the default (2026-10-12, issue #177)

Not asked: README's "Versions" already decides it (the default tracks the newest scenedetect, a minor version; the
previous release stays as a `PySceneDetectVersion` value). Within it, two choices made here: `Bins` and `HashSize`
default to 0 (the release's own default) instead of becoming nullable, which package validation would count as a
breaking change; and `MinOutLength` follows scenedetect's CLI default (0.1 s), not its API's (0), as the library
follows the CLI elsewhere (add-last-scene, min-scene-len as frames).
