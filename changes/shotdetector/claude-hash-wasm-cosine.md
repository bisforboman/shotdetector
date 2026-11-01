### Fixed

- Hash detector (`detect-hash`): the same bits on WebAssembly as on x64. On near-flat frames (dark or faded ones) the
  DCT is rounding noise around its median, and `Math.Cos`, whose last bit differs on browser-wasm, decided some bits:
  the stats' `hash_dist` differed by 2/64 on a few frames of a film. The default sizes' cosine tables are now built
  in, so x64 results are unchanged.
