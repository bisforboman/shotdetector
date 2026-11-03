# Several audio analyses on one decode (2026-11-03)

Issue #718: an ingest pipeline for a broadcast file looks for line-up tone (`ReferenceTone.FindAsync`), draws a
waveform (`WaveformData.ReadAsync`), measures loudness (`Loudness.MeasureAsync`) and looks for silences
(`SilenceFinder`), then converts with a filter that notches out the tone (`Audio.ConvertAsync`). Each opens its own
`AudioReader`, so a long programme's audio is decoded three or four times. The owner chose (2026-11-03) to design an
`AudioAnalysis` now: one `AudioReader`, several analyses, every chunk to all of them, their results together.
Conversion may stay a separate pass, since its filter depends on where the tone was found.

The bar is the one every FrameReader analysis already has: each result is the one its standalone call gives on a
reader opened with the same options, to the bit (`ToneSegment`s, `WaveformData`'s points and `.dat` bytes,
`LoudnessResult`'s doubles, `SilenceSpan`s).

## What the code does today

The analyses don't all read the same samples, and that decides the design.

- **Two ways to read a reader.** `AudioReader.TryRead` gives the decoded frames converted by the reader's swresample
  context to interleaved float32 at the reader's `SampleRate` and `Channels` (by default the stream's own: no
  resampling, a format change only). `AudioReader.TryDecode` (internal) gives the decoded frame as it is (its own
  sample format, rate and layout), trimmed after a seek, with its position in samples. `AudioFilter` reads through
  `TryDecode` and feeds an FFmpeg graph, as ffmpeg's `-af` gets the decoder's frames. Both read the reader on, so a
  host can't use both on one reader: whatever one takes, the other never sees.
- **`ReferenceTone.Find`** reads `TryRead`: the reader's rate and channels, through `ReferenceToneFinder`. It stops
  decoding once `SearchDuration` is read (`IsComplete`). Times count from the first chunk's `Time`.
- **`WaveformData.Read`** reads `TryRead`, through `WaveformBuilder`, which also needs to know whether the decoder
  gives integers or floats (`integerSamples: !audio.DecodesFloat`; `DecodesFloat` is internal), as audiowaveform turns
  each into 16 bits differently.
- **`Loudness.Measure`** reads `TryDecode`: an `AudioFilter` with `ebur128=peak=true+sample`, so ebur128 sees the
  decoded audio, and the reader's rate and channel conversion don't apply (its doc says so). The figures are
  `ffmpeg -af ebur128=peak=true+sample`'s.
- **`SilenceFinder`** has no reader entry point: a host feeds it floats, in practice `TryRead`'s chunks.
  (`AudioFilter` with `silencedetect` is the other way, ffmpeg's own over the decoded frames.)
- `LoudnessMeter`, `WaveformBuilder`, `ReferenceToneFinder` and `SilenceFinder` already take samples a chunk at a
  time (for samples decoded elsewhere, no FFmpeg). The issue's second shape ("sample-based counterparts for waveform
  and reference tone") is therefore there already; what a host can't do with them is get `Loudness.Measure`'s figures
  on the same decode (`LoudnessMeter`'s true peak is BS.1770-4's, not ebur128's 192 kHz resampling, and on a
  resampling or downmixing reader it measures other samples), or call `WaveformBuilder` the way `Read` does without
  `DecodesFloat`.

So yes, they need different samples: loudness the decoded frames, the others the reader's converted floats. One decode
can give both without changing a result: each decoded frame goes once to the loudness graph and once through the
reader's converter, exactly as each standalone path handles it. Nothing is resampled a second time, and nothing is
resampled that wasn't before.

## Question

Which shape should running several audio analyses on one decode take?

## Choices

1. **`AudioAnalysis` with an options record** (recommended): a closed set of the built-in analyses, each turned on by
   giving its options; one call, one result record.
2. **A builder with typed handles**: `var analysis = new AudioAnalysis(reader); var tone = analysis.AddReferenceTone(o);
   ... analysis.Run(); tone.Result`. The same pass inside; analyses can be added one by one, including several of one
   kind (two waveform zooms), and a `Add(Action<AudioChunk>)` for the host's own analyses fits naturally. More types
   to keep stable (FrameReader's API baseline), and results that exist only after `Run`.
3. **A documented pattern only**: make `AudioReader.DecodesFloat` and `Duration` public, and document a host loop that
   feeds `TryRead`'s chunks to `ReferenceToneFinder`, `WaveformBuilder`, `SilenceFinder` and `LoudnessMeter`. Least
   API, but loudness is then `LoudnessMeter`'s (true peak can differ from `Loudness.Measure`'s, and a converting
   reader changes all of it), and every host writes the same loop and its early stop.
4. **A public consumer interface** (`IAudioConsumer` with `Add(chunk)` / `Finish()`), the built-in analyses
   implementing it, hosts adding their own. Most open, but the loudness consumer needs decoded frames rather than
   chunks, so the interface either exposes FFmpeg frames (FrameReader's public API keeps them out) or has two kinds of
   consumer. The same openness comes cheaper as choice 1 or 2 plus a chunk callback.

A second, smaller question: whether the analysis also takes a **chunk callback** (`Action<AudioChunk>`, the reader's
converted chunks, in order) so a host's own analysis (`SpectralStats`-like work, a speech model's feed) rides on the
same decode. It costs one property and no new types; recommended yes.

## Recommended design (choice 1)

```csharp
public sealed record SilenceOptions
{
    public double Level { get; init; } = -60;                           // dBFS, silencedetect's n
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(2);  // silencedetect's d
}

public sealed record AudioAnalysisOptions
{
    public ReferenceToneOptions? ReferenceTone { get; init; } // null: not run; new() for the defaults
    public WaveformOptions? Waveform { get; init; }
    public bool Loudness { get; init; }                       // Loudness.Measure's figures
    public SilenceOptions? Silence { get; init; }
    public Action<AudioChunk>? Chunks { get; init; }          // the host's own analysis (the second question)
    public IProgress<MediaProgress>? Progress { get; init; }  // the whole pass
}

public sealed record AudioAnalysisResult(
    IReadOnlyList<ToneSegment>? ReferenceTone,  // null when not asked for
    WaveformData? Waveform,
    LoudnessResult? Loudness,
    IReadOnlyList<SilenceSpan>? Silences,
    int SampleRate);                            // the reader's: for SilenceSpan.Start(rate)

public static class AudioAnalysis
{
    public static AudioAnalysisResult Run(AudioReader audio, AudioAnalysisOptions options, CancellationToken cancellationToken = default);
    public static Task<AudioAnalysisResult> RunAsync(AudioReader audio, AudioAnalysisOptions options, CancellationToken cancellationToken = default);
}
```

The issue's pipeline:

```csharp
using var audio = new AudioReader("programme.mxf");
var r = await AudioAnalysis.RunAsync(audio, new()
{
    ReferenceTone = new() { SearchDuration = TimeSpan.FromMinutes(3) },
    Waveform = new() { PixelsPerSecond = 100 },
    Loudness = true,
    Silence = new() { Level = -50, Duration = TimeSpan.FromSeconds(1) },
});
using (var dat = File.Create("programme.dat"))
    r.Waveform!.Save(dat);
Console.WriteLine($"{r.Loudness!.Value.Integrated:0.0} LUFS");
string? notch = r.ReferenceTone is [var tone, ..]
    ? FormattableString.Invariant($"equalizer=f=1000:t=q:w=8:g=-40:enable='between(t,{tone.Start.TotalSeconds},{tone.End.TotalSeconds})'")
    : null;
await Audio.ConvertAsync("programme.mxf", "programme.mp3", new() { Filter = notch }); // its own pass, as the issue allows
```

### The pass

Per decoded frame (`TryDecode`: trimmed after a seek, its position):

1. With `Loudness`: into an ebur128 graph as `AudioFilter` feeds it (the frame without its first `skip` samples, at
   its position; frames wholly before the seek point dropped), the sink drained and its output thrown away, as
   `Loudness.Measure` throws it away.
2. With any of the others: through the reader's own converter, as `TryRead` converts it, into one chunk given to the
   tone finder, the waveform builder, the silence finder and the callback, in that order.

At the end of the input: the converter's leftovers as `TryRead`'s last chunk, the graph flushed, each analysis
finished. To make "as `TryRead` converts it" true by construction, `TryRead` and `AudioFilter.TryRead` are split into
"decode a frame" and "use it" (converting, feeding) internally, and the standalone calls and the analysis share those
pieces; no second copy of the conversion or the feeding exists. swresample's output depends only on the frames given
in order, and ebur128's only on the frames and their timestamps, so each sees exactly what it sees standalone.

### Formats, rates and channels

Tone, waveform, silence and the callback share the reader's output: its `SampleRate` and `Channels`, or the stream's
own. That is what each standalone call (or a host feeding `SilenceFinder` from `TryRead`) reads on a reader opened
with the same options, so results match. Loudness keeps reading the decoded frames, untouched by the reader's
conversion, as `Loudness.Measure` does. The waveform builder gets `integerSamples: !DecodesFloat`, as `Read` does.

Analyses wanting different rates or channels on one decode (tone at 16 kHz mono, waveform at the stream's rate) are
not in this design: each extra format would be a converter of its own per frame, and none of today's calls needs it.
A host that wants it opens a second reader. It can come later as a per-analysis `SampleRate`/`Channels` without
changing the shape.

### Results equal to the standalone calls: how tests prove it

`AudioAnalysisTests` (tests/FrameReader.Tests) runs, for each clip, the analysis with every combination of analyses
(one alone, each pair, all four, with the callback) and compares each result with its standalone call on a fresh reader
opened with the same options: `ToneSegment` lists and `LoudnessResult` with record equality (exact doubles),
`WaveformData` by its points and its saved `.dat` bytes (8 and 16 bits), `SilenceSpan` lists against a
`SilenceFinder` fed from `TryRead`, the callback's chunks against `TryRead`'s, sample for sample. The clips: the test
media's integer PCM (16, 24 bit), float PCM, AAC, MP3, Opus, 5.1, a Matroska file with millisecond timestamps (the
timestamp rescaling path), a file with two audio streams (`Stream = 1`), readers with `SampleRate`/`Channels` set
(resampled, downmixed), after `Seek` (inside a frame: the trim), with tone present and a `SearchDuration` shorter and
longer than the clip, a clip with no samples. A test counts the packets the reader reads (an internal counter) to show
one pass where four standalone calls read four. This is FrameReader's own behaviour, not a scenedetect quirk, so it
needs no mutation entry.

### Cancellation and progress

One `CancellationToken`, checked between frames: an `OperationCanceledException` and no results, as each standalone
call. Opening and network waits stay with the reader's `AudioReaderOptions.CancellationToken`, as today.
`AudioAnalysisOptions.Progress` reports the pass (position over the input's duration, about 10 times a second and
once at the end). An analysis's own `Progress` (in `ReferenceToneOptions` or `WaveformOptions`) still reports as it
does standalone, since the finder and builder report from their `Add`: tone's reaches its end at the search duration
while the pass goes on.

### Early stop

Tone with a `SearchDuration` stops taking samples where the standalone call stops decoding; the pass goes on for the
others. If every analysis asked for is complete (tone alone, with a search duration), decoding stops there, so the
analysis is never dearer than the standalone call.

### Errors

The analyses are a closed set, and the pass fails as one, as a decode failure would fail all of them anyway:

- Options are checked before decoding: each finder and builder is made first (bad `SamplesPerPixel`, more than 24
  channels to split, a non-positive `SearchDuration`, a negative silence duration: their `ArgumentException`s).
  `Loudness` checks that the loaded libraries have ebur128 before reading, so a missing filter fails at once, not
  after decoding half the file.
- During the pass a decode or filter failure is a `FrameReaderException`, as standalone; nothing partial is returned.
- Loudness on audio with no samples throws as `Loudness.Measure` does (`InvalidInput`), so the whole call does;
  without `Loudness` the other results come back empty, as their standalone calls give them.
- An exception from the host's callback ends the pass and propagates unchanged.

Per-analysis errors (results with an error each) were considered: with a closed set, every failure except "no
samples" is one that would fail every analysis, and "no samples" is one a caller checks once.

### Stream selection and ranges

One analysis reads one stream: the reader's (`AudioReaderOptions.Stream`, or the best one). Two streams are two
readers and two decodes, as their packets are different; one demuxer feeding two decoders is a later idea, not this.
The pass starts where the reader is: `Seek` first for a range's start, as every standalone call reads "the rest of"
the reader (tone times from the first chunk's time, loudness from the seek point, silences in samples from it). There
is no end bound, as none of the standalone calls has one; an `Until` (stop at a time, every analysis seeing the same
end) is easy to add later, then for the standalone calls too.

### Building blocks first?

They exist: `WaveformBuilder` and `ReferenceToneFinder` already take samples, as `LoudnessMeter` and `SilenceFinder`
do. What this design adds for hosts that keep their own loop (choice 3's case) is small and can go in with it:
`AudioReader.DecodesFloat` and `Duration` made public, so a host can make a `WaveformBuilder` as `Read` makes it.

### Release

Additive FrameReader API (a minor release, no breaking change against the baseline); the README's audio section gets
the example; a `changes/framereader/` fragment.

## Alternatives considered

- **Choices 2-4** above.
- **Per-analysis rates and channels from one decode** (a converter per format): not needed by any of today's calls;
  later, if asked for.
- **Loudness through `LoudnessMeter` on the converted chunks**: drops FFmpeg from the pass, but its true peak isn't
  ebur128's and a converting reader changes the samples; `Loudness.Measure`'s figures would differ.
- **Silence through `silencedetect` in the loudness graph**: ffmpeg's own over the decoded format (integer formats
  compared as integers), but its results are rounded seconds in frame metadata, not `SilenceSpan`s in samples, and it
  would differ from `SilenceFinder` on the same reader. Could be a `Silence.FfmpegExact` option later.
- **Running the analyses on threads of their own**: their work per chunk is small next to decoding; one thread keeps
  the order and the results simple. Decode-ahead belongs to the reader if ever.
- **Including conversion**: its filter depends on where tone was found, so it needs the tone result first; a combined
  pass would have to convert after the tone search, buffering or decoding the start twice. The issue allows it to stay
  separate.
- **Several streams on one demux**: different packets, so no shared decode; a separate idea.

## Answer

**`AudioAnalysis` with an options record**, with the chunk callback (the owner, 2026-11-03): the
recommended design above.

Built as designed (2026-11-03), with two details settled in the code: the callback is an `Action<AudioChunk>` (.NET 9's
`Action<T>` allows a ref struct), and a call that asks for no analysis is an `ArgumentException`, like the other
options' mistakes, rather than a pass that reads for nothing. `AudioAnalysisTests` compares every result with its
standalone call; the packet counter is `AudioReader.PacketsRead`.
