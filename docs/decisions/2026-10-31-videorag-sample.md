# The VideoRag sample's home, FFmpeg libraries and models (2026-10-31)

The owner chose to build the first slice of issue #635 (docs/ideas/dataingestion-video-reader.md, section 3): a sample
with a `VideoDocumentReader` for Microsoft.Extensions.DataIngestion. Three questions the scoping note left open for it.

## Where the sample lives

The brief said `samples/VideoRag/`, but `samples/` is gitignored: it's where tools/make-samples.ps1 writes the
generated test videos that compare.py globs.

### Choices

1. **examples/VideoRag**, next to examples/ShotDetector.Example; `samples/` stays generated clips only.
2. samples/VideoRag, with a `!samples/VideoRag/` exception in .gitignore.

### Answer

**1** (the owner, 2026-10-31).

## How the sample gets FFmpeg's libraries

The stills come from FrameReader's `ImageEncoder`, which needs FFmpeg 8's shared libraries in the process.

### Choices

1. **A folder from `--ffmpeg-libs` or `SHOTDETECTOR_FFMPEG_LIBS`**, as the CLI and the tests take them; without them
   the shots still become sections, with no stills. No LGPL package referenced.
2. A reference to FrameReader.Native (LGPL) from nuget.org, so stills always work on its platforms.

### Answer

**1** (the owner, 2026-10-31).

## Which models the console uses

The enricher's captions and the vector store's embeddings need a chat model and an embedding model.

### Choices

1. **Any OpenAI-compatible endpoint** (Microsoft.Extensions.AI.OpenAI; endpoint, key and model names from environment
   variables): OpenAI, and Ollama or LM Studio through their `/v1` endpoints.
2. Ollama only (OllamaSharp).
3. No provider package: the console prints the sections, the README shows where an `IChatClient` goes.

### Answer

**1** (the owner, 2026-10-31).

Since 2026-11-01 ([2026-11-01-videorag-repo.md](2026-11-01-videorag-repo.md)) the sample lives in its own repository,
bisforboman/shotdetector-videorag, so the first answer above no longer holds.
