# Idea: a video reader for Microsoft.Extensions.DataIngestion (scoping, 2026-10-31)

Issue #635 proposes a video reader for Microsoft.Extensions.DataIngestion (MEDI): a video goes in and comes out as
timestamped chunks for a vector store. The chain would be decode → shots → transcript → sentences →
`IngestionDocument` sections. This note checks what already exists and how the reader would plug in. It then sizes
the gap and the risks. No feature code.

The external facts come from the packages and source themselves. That's **Microsoft.Extensions.DataIngestion
10.11.0-preview.1.26513.7** (the newest on nuget.org on 2026-10-31) and its source at the commit the package names,
`dotnet/extensions@25f07afa`, under `src/Libraries/Microsoft.Extensions.DataIngestion*`.

## 1. What we already have

| Piece | Where | What it gives a reader |
|---|---|---|
| Shots with times | `ShotDetection.Detect(string)` / `Detect(Stream)` / `DetectAsync` / `DetectStreamAsync` (src/ShotDetector/ShotDetection.cs:724-979) | `Shot(Number, Start, End)` (src/ShotDetector/Shots.cs:9). `FrameTime` gives the frame number, seconds and a scenedetect timecode. `Detect(Stream)` already takes the `Stream` MEDI hands a reader (non-faststart MP4 spills to a temp file, `VideoReader.HeadersLast`). |
| Shot lists as text | `Shots.Table`, `Csv`, `Html`, `Json` (src/ShotDetector/Shots.cs:33-169) | Ready-made markdown-ish/JSON renderings. |
| Stills per shot | `Export.SaveImages` (src/ShotDetector/Export.cs:167); FrameReader's `ImageEncoder.Encode(...) → byte[]` (src/FrameReader/ImageEncoder.cs:176, 215) | JPEG/PNG/WebP bytes of a frame. That's exactly what `IngestionDocumentImage.Content` takes. `SaveImages` writes files. `ImageEncoder` returns bytes, but only in-process (FFmpeg's libraries loaded). |
| Frame access | FrameReader `VideoFrameReader`, `FrameDecoder` (seek, deinterlace, autorotate) | A frame at a shot's start/middle for the still. |
| Audio | FrameReader `AudioReader`, `AudioFilter` (src/FrameReader/AudioReader.cs:57, AudioFilter.cs:21) | Decoded, resampled PCM: the 16 kHz mono input a Whisper transcriber needs, without an ffmpeg call. |
| Pure-managed path | shotdetector-web's `ShotDetectorWeb.Core` (MIT): ManagedCodecs decodes, FastYuv converts, `ShotDetection.Detect(YuvVideo, IEnumerable<YuvFrame>)` detects | Shots with no FFmpeg at all, but only for 8-bit 4:2:0 H.264 and MJPEG in MP4/AVI, and with no stills or audio yet. FastYuv is LGPL. |

Not ours and not here: transcription (Whisper.net, a separate MIT package) and the text side. The issue leans on
StanzaSharp's DataIngestion chunker idea (bisforboman/StanzaSharp#233) for sentences and entities.

## 2. How a reader plugs into MEDI (verified from source)

- **Contract**: `public abstract class IngestionDocumentReader` in Microsoft.Extensions.DataIngestion.Abstractions.
  It has one abstract method:
  `Task<IngestionDocument> ReadAsync(Stream source, string identifier, string mediaType, CancellationToken)`.
  There are also virtual `ReadAsync(FileInfo, string identifier, string? mediaType, ...)` overloads, which open a
  FileStream and call the Stream one. A file reader overrides the `FileInfo` one, as `MarkItDownReader` does; we'd
  do the same, so ffmpeg can seek in the file instead of reading a pipe.
- **Document shape**: `IngestionDocument(identifier)` holds `IList<IngestionDocumentSection> Sections`. A section
  holds `Elements`. Element types are `Paragraph`, `Header` (with `Level`), `Footer`, `Table`, `Image` (with
  `Content` as `ReadOnlyMemory<byte>?`, plus `MediaType` and `AlternativeText`) and nested `Section`. Every element
  has `Text`, markdown, `PageNumber` and a free `Metadata` dictionary.
- **Pipeline**: `IngestionPipeline<T>(IngestionDocumentReader reader, IngestionChunker<T> chunker,
  IngestionChunkWriter<T> writer, ...)` runs the reader, then `DocumentProcessors`, the chunker, `ChunkProcessors` and
  the writer. `VectorStoreWriter` writes to any `Microsoft.Extensions.VectorData` collection.
- **What it expects of us, and two findings that shape the design**:
  - **Element metadata doesn't reach the chunks.** The chunkers (`ElementsChunker`, `SectionChunker`,
    `HeaderChunker`) never copy `element.Metadata` into `IngestionChunk.Metadata`. `VectorStoreWriter` stores only
    the chunk's own metadata. So a start time put on an element is lost by the time the chunk is stored. What
    survives is the chunk's **context**: `SectionChunker` uses a section's first `Header` as the context of every
    chunk in it, and `VectorStoreWriter` stores that context. So a reader that makes **one section per shot (or
    scene), headed `## 00:12:34.500 – 00:12:41.200`**, gets the timestamp into every stored record with no custom
    chunker. The alternative is a small `IngestionChunkProcessor` that copies the time into chunk metadata, which
    makes it a filterable field.
  - **Stills become searchable text through MEDI's own enricher.** `ImageAlternativeTextEnricher` (a document
    processor taking an `IChatClient`) captions every `IngestionDocumentImage` that has `Content` and `MediaType`
    but no `AlternativeText`. The chunkers then embed `AlternativeText ?? Text`, and skip an image that has neither.
    So keyframe JPEGs per shot plus that enricher give visual search with no transcript at all.
- **Version and stability**: every MEDI package is `<Stage>preview</Stage>` and ships monthly
  (10.0.0-preview.1 in November 2025 … 10.11.0-preview.1 in October 2026, 15 versions). Its API can still change. The packages target net10.0, net9.0,
  net8.0, net462 and netstandard2.0, and both assemblies carry `IsTrimmable` metadata. Licence MIT.
- **Existing readers**: only `MarkItDownReader` (MarkItDown executable or MCP) and `MarkdownReader` (Markdig) ship in
  dotnet/extensions. There's no audio or video reader, which the issue says too.

## 3. The gap, and a first slice

The gap is a class `VideoDocumentReader : IngestionDocumentReader` (we have none), plus a sample that runs it through
`IngestionPipeline`. Detection, timestamps, stills and audio decoding all exist. What's missing is glue, a decision on
where it lives, and the text side (transcripts).

**First thin slice (1 PR):** a **sample**, not a package, matching the issue's "sample project first". Something like
`samples/VideoRag/`, outside `ShotDetector.slnx`'s packed projects. It contains:

1. `VideoDocumentReader` (about 100-150 lines). It overrides `ReadAsync(FileInfo, ...)` and `ReadAsync(Stream, ...)`
   and runs `ShotDetection.DetectAsync`. It makes one `IngestionDocumentSection` per shot, each with a
   `Header("[start – end]")`, a `Paragraph` with the shot number and times, and an `IngestionDocumentImage` holding
   the shot's middle frame as JPEG bytes (FrameReader `ImageEncoder`). It also puts `start`/`end` seconds in the
   element metadata for later use.
2. A console app: `IngestionPipeline<string>` with that reader, `ImageAlternativeTextEnricher`, `SectionChunker` and
   `VectorStoreWriter` on the in-memory vector store. It answers a question with "shot 12, 00:12:34".
3. A unit test that reads the test clips (150 frames, cuts at 50/100) and gets 3 sections with the right header
   times. It needs no chat client: the enricher stays in the sample, not the test.

The test needs no network or model, but the sample's enricher and embeddings need an `IChatClient` and an embedding
generator, configured by the user. It doesn't touch the core, so parity, AOT and mutation don't change.

**Size:** the slice is **1 PR**. A full showcase is **3-4 PRs** more:

- transcript sections from Whisper.net over `AudioReader` PCM, with segments merged into shots by time (1 PR);
- grouping shots into scenes, e.g. by similar captions or long-gap merges (1 PR);
- the StanzaSharp chunker once StanzaSharp#233 exists (1 PR, in that repository or here);
- packaging as `ShotDetector.DataIngestion`, only if people ask (1 PR plus a release).

## 4. Risks

- **Licensing.** MEDI is MIT, and ShotDetector, FrameReader, the CLI and shotdetector-web are MIT, so the reader
  itself is MIT. The LGPL parts are optional:
  - stills through `ImageEncoder` and in-process decoding load FFmpeg's shared libraries, `FrameReader.Native.<rid>`
    (LGPL-2.1-or-later);
  - the pure-managed route needs `ShotDetector.FastYuv` (LGPL-2.1-or-later).

  Without either, detection runs through an `ffmpeg` executable on PATH, and stills would come from `ffmpeg -frames:v
  1` (as `Export.SaveImages` does on that path). A future package must not depend on an LGPL package by default. The
  sample can reference `FrameReader.Native`, saying so. Whisper.net is MIT, but its native whisper.cpp runtimes come
  per RID.
- **Upstream acceptance.** dotnet/extensions adds APIs through API proposals and API review (CONTRIBUTING.md, "API
  Review Process"). The readers it ships wrap general-purpose tools (MarkItDown, Markdig). A reader that brings
  FFmpeg natives, or depends on an ffmpeg executable, is unlikely to be taken into Microsoft.Extensions.*. The
  realistic home is our own repository or package, linked from their docs or samples at most. Plan for it to stay
  ours.
- **Preview churn.** MEDI is still preview with monthly versions. A sample pinned to one version can break on the
  next. That's acceptable for a sample, but a reason not to publish a package yet.
- **Runtime constraints.**
  - Natives exist for linux-x64, linux-arm64, linux-musl-x64, win-x64 and osx-arm64 (src/FrameReader.Native). On
    other RIDs the reader would need ffmpeg on PATH.
  - Our libraries are `IsAotCompatible`, and MEDI's assemblies are marked trimmable. But `IngestionPipeline` pulls in
    Microsoft.Extensions.AI, VectorData and Microsoft.ML.Tokenizers, so the sample's AOT status depends on them.
    Treat AOT as untested, not a goal of the slice.
  - Our packages are net10.0 only, so the reader would be too. MEDI supports net10.0.
  - ARM results differ slightly from x86 (CLAUDE.md, "scenedetect on ARM"), which doesn't matter for RAG.
- **Audience.** As the issue says, video RAG is mostly Python. This is a showcase, not an adoption driver
  (rank 7 of 9).

## 5. Verdict: **park, with the owner deciding**

The integration is cheap and clean: one abstract method, a section per shot carrying its time in the header, and
MEDI's own image enricher captioning the stills. The slice is 1 PR. But the issue is a showcase ranked 7 of 9, MEDI
is still preview, and the full idea waits on StanzaSharp#233. So we'd park it unless the owner wants the demo now. If
they do, the sample slice above is the start.

Decision questions for the owner:

1. Build the sample slice now, or park until StanzaSharp#233 and a stable MEDI release?
2. Where should it live: `samples/` in this repository, shotdetector-web, or a new showcase repository tying the three
   projects together?
3. Should the sample show only stills plus captions (needs an `IChatClient`), or wait for the transcript slice
   (Whisper.net) so it works without a vision model?
4. Should the sample use our FFmpeg build (`FrameReader.Native`, LGPL, stills in-process) or an `ffmpeg` executable
   (no LGPL package, a process per still)?
5. Should it ever become a package (`ShotDetector.DataIngestion`), or stay a sample until someone asks?
