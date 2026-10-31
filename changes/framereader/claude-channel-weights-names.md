### New

- `ChannelWeights.ForLayout` also takes layouts FFmpeg doesn't name, as it describes them (`StreamInfo.ChannelLayout`):
  `5 channels (FL+FR+LFE+BL+BR)`, the channel names alone in the samples' order, or `N channels` for channels without
  positions. So `LoudnessMeter` weighs any layout as ebur128 does (issue #646).
