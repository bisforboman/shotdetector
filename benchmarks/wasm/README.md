# benchmarks/wasm

ShotDetector's frame-source path (`ShotDetection.Detect(YuvVideo, IEnumerable<YuvFrame>, ...)` with FastYuv's
`SwscaleYuv420`, what a page that decodes in the browser runs) on browser-wasm, compiled ahead of time, under Node, and
the same code natively. `benchmark.yml` runs it weekly on x64 and records it as the run's `wasm` section on the
bench-data branch (schema: `../bench-data.md`); its step summary has the table.

Why: the WebAssembly build is a product of its own (shotdetector-web), and neither its speed nor its results show
anywhere else. Its CSVs must equal the native run's on the same frames, so a result that only WebAssembly gets wrong
fails the step; the timings show a slowdown week to week, and the native run beside it says how far behind WebAssembly
is on the same runner.

- `Bench.cs`: the benchmark, shared by both hosts. For each input, the content and adaptive detectors and content with
  stats, best of 3 runs by wall time; one line of JSON on stdout with each case's frames, wall time, the time spent
  reading frames from the file, detection time (wall minus reading), nanoseconds per pixel (of detection), the managed
  heap's peak (`GC.GetTotalMemory`, sampled every frame), shots and the SHA-256 of the scene and stats CSVs.
- `Wasm/`: the browser-wasm app (`main.mjs` starts it under Node and reads the host's files for it in slices, a frame at
  a time: Emscripten's file system doesn't see them). Needs the wasm-tools workload, so it isn't in ShotDetector.slnx.
- `Native/`: the same, natively, with `RandomAccess` (in the solution, so CI builds `Bench.cs`).

The frames are decoded once, before the runs, into raw yuv420p (`ffmpeg -frames:v N -f rawvideo -pix_fmt yuv420p`), so
no decoder's time is in the numbers. The reads stay out of ns/pixel: a raw 1080p frame is 3 MB, and copying it from the
file takes a large share of the time, natively too, where a decoder in the same process would hand its planes over.

```sh
dotnet workload install wasm-tools
dotnet publish benchmarks/wasm/Wasm -c Release -p:RunAOTCompilation=true
dotnet build benchmarks/wasm/Native -c Release
ffmpeg -i film.mp4 -frames:v 500 -f rawvideo -pix_fmt yuv420p film.yuv
input=film=film.yuv,1920,1080,24/1,500,bt709   # name=path,width,height,rate,frames,color_space (ffprobe's)
dotnet benchmarks/wasm/Native/bin/Release/net10.0/ShotDetector.WasmBench.Native.dll "$input" > native.json
node benchmarks/wasm/Wasm/bin/Release/net10.0/browser-wasm/AppBundle/main.mjs "$input" | grep -v '^MONO_WASM:' > wasm.json
python tools/bench_record.py --wasm-report --wasm wasm.json --wasm-native native.json
```

An AOT build takes minutes and its `obj/` holds IL stripped for AOT: delete `Wasm/bin` and `Wasm/obj` before an
interpreted build after it, and afterwards (they take a few hundred MB).
