### New

- The time options as `TimeSpan`s, beside scenedetect's strings: `DetectionOptions.Start`, `End`, `Duration`,
  `MinSceneDuration` and `MinOutDuration` (for `StartTime`, `EndTime`, `RangeDuration`, `MinSceneLength`,
  `MinOutLength`), and `DetectorSettings.MinSceneDuration`/`MinOutDuration`. Set one form of each option; both is an
  `ArgumentException`.
