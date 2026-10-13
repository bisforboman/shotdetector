### New

- FrameReader: `FFmpegLibraries.MissingFilters(filterGraph)`, the filters a graph of your own uses that the libraries
  lack, in `Missing`'s format (`"filter equalizer"`), so a startup check concatenates the two lists instead of keeping
  a hand list; `"filter graph"` when the graph doesn't parse. The graph is only parsed, nothing is created
  (issue #236).
