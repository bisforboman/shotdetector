### New

- `DetectionOptions.ProgressStep` (e.g. `0.1`): progress is reported only when the fraction done crosses a step, besides
  the first report and the last, so a service log gets one line per 10 % without a throttle of its own (issue #238).
