using System.Diagnostics;
using System.Globalization;
using FrameReader;

/// <summary>
/// PictureMeter against FFmpeg 8.1's own filters on the same frames: each clip is measured from the decoder's own planes
/// (<see cref="VideoFrameReader.TryTake"/>, as coded: no autorotate, <see cref="PictureFormat.Of"/>) and run through the
/// ffmpeg executable beside the libraries with signalstats, cropdetect (per frame, and with its defaults over the clip)
/// and blackdetect (d=0 and its default 2 s), which our FFmpeg build doesn't carry. YMIN and YMAX equal, YAVG and BRNG to
/// the digits FFmpeg prints, cropdetect's eight figures equal, blackdetect's spans equal to the digits printed (-copyts:
/// the stream's own pts), the picture types ffprobe's. At 8 and 10 bits, 4:2:0, 4:2:2 and 4:4:4, limited and full range.
/// Colour bars, which no FFmpeg filter finds, by what each clip holds. Needs SHOTDETECTOR_FFMPEG_LIBS (an FFmpeg 8.1
/// shared build's folder with ffmpeg and ffprobe); without it these do nothing.
/// </summary>
public class PictureMeterTests
{
    static readonly string? Libs = TestLibraries.Load();

    const string Ffv1 = "-c:v ffv1 -level 3";

    [Theory]

    // smptehdbars, black, testsrc2: bars for the first 2 s (50 frames), black for 2.5 s.
    [InlineData("bars.nut", "-f lavfi -i smptehdbars=s=640x360:r=25:d=2 -f lavfi -i color=black:s=640x360:r=25:d=2.5 -f lavfi -i testsrc2=s=640x360:r=25:d=1 -filter_complex [0][1][2]concat=n=3:v=1:a=0,format=yuv420p -c:v rawvideo", 0, 0, 50)]

    // The same in full range (yuvj420p): blackdetect's threshold 25, the bars' full-range colours.
    [InlineData("full.nut", "-f lavfi -i smptehdbars=s=640x360:r=25:d=2 -f lavfi -i color=black:s=640x360:r=25:d=2.5 -f lavfi -i testsrc2=s=640x360:r=25:d=1 -filter_complex [0][1][2]concat=n=3:v=1:a=0,scale=out_range=pc,format=yuvj420p -c:v rawvideo", 0, 0, 50)]

    // PAL 75% bars (EBU's 100/0/75/0: 100% white), then SMPTE bars (BT.601), all bars.
    [InlineData("pal.nut", "-f lavfi -i pal75bars=s=720x576:r=25:d=1 -f lavfi -i smptebars=s=720x576:r=25:d=1 -filter_complex [0][1]concat=n=2:v=1:a=0,format=yuv420p -c:v rawvideo", 0, 0, 50)]

    // Letterboxed (black above and below, a column of black on the left), fading in from black: cropdetect's lines.
    [InlineData("letterbox.nut", "-f lavfi -i testsrc2=s=600x360:r=25:d=3 -vf pad=640:480:40:60,fade=in:0:12,format=yuv420p -c:v rawvideo", 0, -1, -1)]

    // The same rotated a quarter turn (a display matrix): cropdetect's lines in the upright picture; H.264's I, P, B.
    [InlineData("rotated.mp4", "-f lavfi -i testsrc2=s=600x360:r=25:d=3 -vf pad=640:480:40:60,fade=in:0:12,format=yuv420p -c:v libx264 -qp 0 -bf 2", 90, -1, -1)]

    // Bars turned a quarter turn clockwise when coded, shown upright by a -90 rotation.
    [InlineData("rotated270.mp4", "-f lavfi -i smptehdbars=s=640x360:r=25:d=1 -f lavfi -i testsrc2=s=640x360:r=25:d=1 -filter_complex [0][1]concat=n=2:v=1:a=0,pad=640:480:0:60,transpose=cclock,format=yuv420p -c:v libx264 -qp 0", -90, 0, 25)]

    // Black but for the last row and column, then the first row and column too: cropdetect doesn't look at the last line
    // for the first one with content.
    [InlineData("lines.nut", "-f lavfi -i color=black:s=64x48:r=25:d=1 -vf drawbox=x=0:y=ih-1:w=iw:h=1:color=white:t=fill,drawbox=x=iw-1:y=0:w=1:h=ih:color=white:t=fill:enable=gte(t\\,0.5),drawbox=x=0:y=0:w=iw:h=1:color=white:t=fill:enable=gte(t\\,0.8),format=yuv420p -c:v rawvideo", 0, -1, -1)]

    // Luma 38 then 37 (to the end): blackdetect's threshold, 16 + 0.1 × 219 truncated to 37; a black span at the end ends
    // at the last frame. Full range: 26 then 25 (0.1 × 255 truncated).
    [InlineData("nearblack.nut", "-f lavfi -i color=black:s=160x90:r=25:d=0.5 -f lavfi -i color=black:s=160x90:r=25:d=2 -filter_complex [0]lutyuv=y=38:u=128:v=128[a];[1]lutyuv=y=37:u=128:v=128[b];[a][b]concat=n=2:v=1:a=0,format=yuv420p -c:v rawvideo", 0, -1, -1)]
    [InlineData("nearblackfull.nut", "-f lavfi -i color=black:s=160x90:r=25:d=0.5 -f lavfi -i color=black:s=160x90:r=25:d=2 -filter_complex [0]format=yuvj420p,lutyuv=y=26:u=128:v=128[a];[1]format=yuvj420p,lutyuv=y=25:u=128:v=128[b];[a][b]concat=n=2:v=1:a=0 -c:v rawvideo", 0, -1, -1)]

    // Two white frames, then letterboxed: cropdetect's defaults leave out the first two frames.
    [InlineData("skip.nut", "-f lavfi -i color=white:s=640x480:r=25:d=0.08 -f lavfi -i testsrc2=s=600x360:r=25:d=1 -filter_complex [1]pad=640:480:40:60[b];[0][b]concat=n=2:v=1:a=0,format=yuv420p -c:v rawvideo", 0, -1, -1)]

    // An odd size, out-of-range luma and chroma (BRNG), the chroma plane's last column and row half covered.
    [InlineData("odd.nut", "-f lavfi -i testsrc=s=321x241:r=25:d=2 -vf lutyuv=y=val*1.2:u=val*1.4-50:v=val*0.7+60,fade=in:0:8,format=yuv420p -c:v rawvideo", 0, -1, -1)]

    // 8-bit 4:2:2 and 4:4:4: BRNG's chroma under each pixel at its own subsampling.
    [InlineData("odd422.nut", "-f lavfi -i testsrc=s=321x241:r=25:d=1 -vf lutyuv=y=val*1.2:u=val*1.4-50:v=val*0.7+60,format=yuv422p -c:v rawvideo", 0, -1, -1)]
    [InlineData("odd444.nut", "-f lavfi -i testsrc=s=321x241:r=25:d=1 -vf lutyuv=y=val*1.2:u=val*1.4-50:v=val*0.7+60,format=yuv444p -c:v rawvideo", 0, -1, -1)]

    // 10 bits, 4:2:2 (ProRes's and DNxHR's layout): bars, black (threshold 151) and letterboxed content.
    [InlineData("bars422p10.mkv", "-f lavfi -i smptehdbars=s=640x360:r=25:d=1 -f lavfi -i color=black:s=640x360:r=25:d=2.5 -f lavfi -i testsrc2=s=600x300:r=25:d=1 -filter_complex [2]pad=640:360:40:30[c];[0][1][c]concat=n=3:v=1:a=0,format=yuv422p10le " + Ffv1, 0, 0, 25)]

    // 10 bits in full range (color_range pc): blackdetect's threshold 102, the bars' full-range colours.
    [InlineData("full422p10.mkv", "-f lavfi -i smptehdbars=s=640x360:r=25:d=1 -f lavfi -i color=black:s=640x360:r=25:d=2.5 -filter_complex [0][1]concat=n=2:v=1:a=0,scale=out_range=pc,format=yuv422p10le -color_range pc " + Ffv1, 0, 0, 25)]

    // 10 bits, 4:2:0 and 4:4:4, out of range (BRNG over 64 to 940 luma), an odd size, fading in.
    [InlineData("odd420p10.mkv", "-f lavfi -i testsrc=s=321x241:r=25:d=1 -vf lutyuv=y=val*1.2:u=val*1.4-50:v=val*0.7+60,fade=in:0:8,format=yuv420p10le " + Ffv1, 0, -1, -1)]
    [InlineData("odd444p10.mkv", "-f lavfi -i testsrc=s=321x241:r=25:d=1 -vf lutyuv=y=val*1.2:u=val*1.4-50:v=val*0.7+60,format=yuv444p10le " + Ffv1, 0, -1, -1)]

    // 10-bit luma 152 then 151, cropdetect's 10-bit limit (96) by a dim band: thresholds scaled to the depth.
    [InlineData("nearblack10.mkv", "-f lavfi -i color=black:s=160x90:r=25:d=0.5 -f lavfi -i color=black:s=160x90:r=25:d=2 -filter_complex [0]format=yuv420p10le,lutyuv=y=152:u=512:v=512,drawbox=y=20:h=4:color=0x2a2a2a:t=fill[a];[1]format=yuv420p10le,lutyuv=y=151:u=512:v=512[b];[a][b]concat=n=2:v=1:a=0 " + Ffv1, 0, -1, -1)]

    // 12 bits, 4:2:2, rotated: the deepest delivery layout ffmpeg's filters take here.
    [InlineData("rotated422p12.mkv", "-f lavfi -i testsrc2=s=600x360:r=25:d=1 -vf pad=640:480:40:60,format=yuv422p12le " + Ffv1, 90, -1, -1)]
    public void IsFfmpegsFilters(string name, string encode, int rotation, int barsFirst, int barsEnd)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-picture-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            string coded = rotation == 0 ? path : Path.Combine(dir, "coded-" + name);
            Ffmpeg(["-v", "error", "-y", .. encode.Split(' '), coded]);
            if (rotation != 0)
                Ffmpeg("-v", "error", "-y", "-display_rotation", rotation.ToString(CultureInfo.InvariantCulture), "-i", coded, "-c", "copy", path);

            var ours = Measure(path, rotation, out var format);
            var theirs = Filters(path);

            // 4:2:2 turned a quarter turn can't stay 4:2:2: ffmpeg's autorotate converts it to 4:4:4 (swscale, bicubic)
            // before transpose, so its BRNG counts interpolated chroma. The meter's is the planes' as decoded, ffmpeg's
            // with -noautorotate.
            var brng = rotation % 180 != 0 && format.Chroma == ChromaSubsampling.Yuv422 ? Filters(path, autorotate: false).Frames : theirs.Frames;
            Assert.Equal(theirs.Frames.Count, ours.Count);
            for (int i = 0; i < ours.Count; i++)
            {
                var f = theirs.Frames[i];
                Assert.True(int.Parse(f["lavfi.signalstats.YMIN"], CultureInfo.InvariantCulture) == ours.YMin(i), $"frame {i} YMIN {ours.YMin(i)}, FFmpeg's {f["lavfi.signalstats.YMIN"]}");
                Assert.True(int.Parse(f["lavfi.signalstats.YMAX"], CultureInfo.InvariantCulture) == ours.YMax(i), $"frame {i} YMAX {ours.YMax(i)}, FFmpeg's {f["lavfi.signalstats.YMAX"]}");
                Assert.True(Printed(f["lavfi.signalstats.YAVG"], ours.YAvg(i)), $"frame {i} YAVG {ours.YAvg(i)}, FFmpeg's {f["lavfi.signalstats.YAVG"]}");
                Assert.True(Printed(brng[i]["lavfi.signalstats.BRNG"], ours.Brng(i)), $"frame {i} BRNG {ours.Brng(i)}, FFmpeg's {brng[i]["lavfi.signalstats.BRNG"]}");
                var c = ours.FrameCrop(i);
                int[] crop = [.. new[] { "x1", "x2", "y1", "y2", "w", "h", "x", "y" }.Select(k => int.Parse(f["lavfi.cropdetect." + k], CultureInfo.InvariantCulture))];
                Assert.Equal(crop, (int[])[c.X1, c.X2, c.Y1, c.Y2, c.W, c.H, c.X, c.Y]);
            }

            Assert.Equal(theirs.Crop, ours.Crop()?.ToString());
            double tb = ours.TimeBase.Num / (double)ours.TimeBase.Den;
            foreach (var (d, spans) in new[] { (0.0, theirs.Black0), (2.0, theirs.Black2) })
            {
                var mine = ours.BlackSpans(d);
                Assert.True(mine.Count == spans.Count, $"blackdetect d={d}: {mine.Count} spans, FFmpeg's {spans.Count}");
                foreach (var (m, s) in mine.Zip(spans))
                    Assert.True(Printed(s.Start, m.StartPts * tb) && Printed(s.End, m.EndPts * tb), $"blackdetect d={d}: {m}, FFmpeg's {s}");
            }

            Assert.Equal(PictureTypes(path), ours.Types());
            if (barsFirst < 0)
                Assert.Empty(ours.BarsSpans());
            else
                Assert.Equal([(barsFirst, barsEnd)], ours.BarsSpans().Select(s => (s.FirstFrame, s.EndFrame)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // A run split into ranges, each measured by its own meter (handed over as bytes), joins into the whole run's
    // measures byte for byte; damaged bytes are refused. At 10 bits, so the depth goes through the bytes too.
    [Fact]
    public void RangesJoinIntoTheWhole()
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-picture-").FullName;
        try
        {
            string path = Path.Combine(dir, "join.mkv");
            Ffmpeg(["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x240:r=25:d=2", "-vf", "fade=in:0:10,format=yuv422p10le", .. Ffv1.Split(' '), path]);
            var whole = Measure(path, 0, out var format);
            Assert.Equal(10, whole.BitDepth);
            int[] cuts = [0, 13, 31, whole.Count];
            var parts = new List<PictureMeasures>();
            for (int r = cuts.Length - 2; r >= 0; r--)
            {
                // Given from the range's first frame on, past its end too: those are left out.
                var meter = new PictureMeter(format, cuts[r], cuts[r + 1]);
                Feed(path, meter, cuts[r]);
                parts.Add(PictureMeasures.FromBytes(meter.Measures.ToBytes()));
            }

            Assert.Equal(whole.ToBytes(), PictureMeasures.Join(parts)!.ToBytes());
            Assert.Null(PictureMeasures.Join(parts.Where(p => p.FirstFrame != 0)));
            Assert.Equal(13, PictureMeasures.Join(parts.Where(p => p.FirstFrame != 13))!.Count); // a gap ends the join
            var bytes = whole.ToBytes();
            bytes[20] ^= 1;
            Assert.Throws<InvalidDataException>(() => PictureMeasures.FromBytes(bytes));
            Assert.Equal(whole.Take(5).ToBytes(), PictureMeasures.Join([whole.Take(5)])!.ToBytes());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // blackdetect's pix_th and pic_th other than its defaults: the meter counts black pixels at its own pixel threshold
    // and the measures judge frames at any picture threshold, giving blackdetect's spans (d=0, every run) for each pair;
    // the threshold travels with the measures' bytes (version 1's bytes at the default), and ranges of other thresholds
    // don't join. 8-bit limited and full range, 10-bit, fading through grey.
    [Theory]
    [InlineData("fade.nut", "-f lavfi -i testsrc2=s=160x90:r=25:d=2 -vf fade=out:st=0.5:d=1,format=yuv420p -c:v rawvideo")]
    [InlineData("fadefull.nut", "-f lavfi -i testsrc2=s=160x90:r=25:d=2 -vf fade=out:st=0.5:d=1,format=yuvj420p -c:v rawvideo")]
    [InlineData("fade10.mkv", "-f lavfi -i testsrc2=s=160x90:r=25:d=2 -vf fade=out:st=0.5:d=1,format=yuv422p10le " + Ffv1)]
    public void ThresholdsAreBlackdetects(string name, string encode)
    {
        if (Libs is null)
            return;
        string dir = Directory.CreateTempSubdirectory("framereader-black-").FullName;
        try
        {
            string path = Path.Combine(dir, name);
            Ffmpeg(["-v", "error", "-y", .. encode.Split(' '), path]);
            _ = Measure(path, 0, out var format);
            foreach (var (pix, pic) in new[] { (0.1, 0.98), (0.3, 0.98), (0.3, 0.6), (0.05, 0.5), (0.0, 1.0), (1.0, 0.0) })
            {
                var meter = new PictureMeter(format, 0, int.MaxValue, pix);
                Feed(path, meter, 0);
                var measures = meter.Measures;
                Assert.Equal(pix, measures.BlackPixelThreshold);
                string log = Ffmpeg(["-hide_banner", "-nostats", "-nostdin", "-copyts", "-i", path, "-map", "0:v:0",
                    "-vf", FormattableString.Invariant($"blackdetect=d=0:pix_th={pix}:pic_th={pic}"), "-f", "null", "-"]);
                var theirs = log.Split('\n').Where(l => l.Contains("black_start:", StringComparison.Ordinal))
                    .Select(l => l[l.IndexOf("black_start:", StringComparison.Ordinal)..].Split(' ').Select(w => w.Split(':')[1]).ToArray())
                    .ToList();
                var mine = measures.BlackSpans(0, pic);
                double tb = measures.TimeBase.Num / (double)measures.TimeBase.Den;
                Assert.True(mine.Count == theirs.Count, $"pix_th={pix} pic_th={pic}: {mine.Count} spans, FFmpeg's {theirs.Count}");
                foreach (var (m, s) in mine.Zip(theirs))
                    Assert.True(Printed(s[0], m.StartPts * tb) && Printed(s[1], m.EndPts * tb), $"pix_th={pix} pic_th={pic}: {m}, FFmpeg's {string.Join(' ', s)}");

                var back = PictureMeasures.FromBytes(measures.ToBytes());
                Assert.Equal(pix, back.BlackPixelThreshold);
                Assert.Equal(measures.ToBytes(), back.ToBytes());
            }

            var standard = new PictureMeter(format);
            Feed(path, standard, 0);
            Assert.Equal(0x4D505246U, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(standard.Measures.ToBytes())); // version 1

            // The rest of the run at another threshold: not joined.
            var rest = new PictureMeter(format, 10, int.MaxValue, 0.3);
            Feed(path, rest, 10);
            Assert.True(rest.Measures.Count > 0);
            Assert.Equal(10, PictureMeasures.Join([standard.Measures.Take(10), rest.Measures])!.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => new PictureMeter(format, 0, 1, 1.5));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PictureMeter(format, 0, 1, double.NaN));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // Colour bars by definition, on planes made here: the seven 75% BT.601 bars, the same with one bar missing, and the
    // seven at 10 bits in 4:4:4.
    [Fact]
    public void BarsAreTheSevenInOrder()
    {
        byte[,] bars = { { 180, 128, 128 }, { 162, 44, 142 }, { 131, 156, 44 }, { 112, 72, 58 }, { 84, 184, 198 }, { 65, 100, 212 }, { 35, 212, 114 } };
        Assert.True(Bars(bars, 7, 8, ChromaSubsampling.Yuv420));
        Assert.False(Bars(bars, 6, 8, ChromaSubsampling.Yuv420));
        Assert.True(Bars(bars, 7, 10, ChromaSubsampling.Yuv444));
        Assert.True(Bars(bars, 7, 10, ChromaSubsampling.Yuv422));

        static bool Bars(byte[,] colours, int count, int depth, ChromaSubsampling chroma)
        {
            const int w = 280, h = 80;
            var format = new PictureFormat(w, h, false, new Rational(1, 25), BitDepth: depth, Chroma: chroma);
            int bytes = format.BytesPerSample, shift = depth - 8, cw = format.ChromaWidth, ch = format.ChromaHeight;
            var y = new byte[w * h * bytes];
            var u = new byte[cw * ch * bytes];
            var v = new byte[cw * ch * bytes];
            void Put(byte[] plane, int at, int value)
            {
                if (bytes == 1)
                {
                    plane[at] = (byte)value;
                }
                else
                {
                    plane[2 * at] = (byte)(value << shift);
                    plane[(2 * at) + 1] = (byte)((value << shift) >> 8);
                }
            }

            for (int x = 0; x < w; x++)
            {
                int bar = Math.Min(x * count / w, count - 1);
                for (int r = 0; r < h; r++)
                    Put(y, (r * w) + x, colours[bar, 0]);
                int cx = x * cw / w;
                for (int r = 0; r < ch; r++)
                {
                    Put(u, (r * cw) + cx, colours[bar, 1]);
                    Put(v, (r * cw) + cx, colours[bar, 2]);
                }
            }

            var meter = new PictureMeter(format);
            meter.Add(y, w * bytes, u, cw * bytes, v, cw * bytes, 0, PictureType.I);
            Assert.Equal("I", meter.Measures.Types());
            return meter.Measures.IsBars(0);
        }
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        var format = new PictureFormat(16, 8, false, new Rational(1, 25));
        Assert.Throws<ArgumentException>(() => new PictureMeter(format with { Width = 0 }));
        Assert.Throws<ArgumentException>(() => new PictureMeter(format with { TimeBase = new Rational(0, 1) }));
        Assert.Throws<ArgumentException>(() => new PictureMeter(format with { Rotation = 45 }));
        Assert.Throws<ArgumentException>(() => new PictureMeter(format with { BitDepth = 17 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PictureMeter(format, 5, 4));
        var meter = new PictureMeter(format);
        Assert.Throws<ArgumentException>(() => meter.Add(new byte[16 * 7], 16, new byte[8 * 4], 8, new byte[8 * 4], 8, 0));
        meter.Add(new byte[16 * 8], 16, new byte[8 * 4], 8, new byte[8 * 4], 8, 0);
        Assert.Equal(1, meter.Measures.Count);
        Assert.True(meter.Measures.IsBlack(0));
        Assert.Equal("?", meter.Measures.Types());
        var deep = new PictureMeter(format with { BitDepth = 10, Chroma = ChromaSubsampling.Yuv422 });
        Assert.Throws<ArgumentException>(() => deep.Add(new byte[16 * 8], 16, new byte[8 * 8], 8, new byte[8 * 8], 8, 0)); // 8-bit rows
        deep.Add(new byte[2 * 16 * 8], 32, new byte[2 * 8 * 8], 16, new byte[2 * 8 * 8], 16, 0);
        Assert.Equal(16, deep.Measures.Width);
    }

    // The decoder's own planes (as coded: no autorotate), what FFmpeg's filters see.
    static PictureMeasures Measure(string path, int rotation, out PictureFormat format)
    {
        using var reader = Reader(path);
        PictureMeter? meter = null;
        PictureFormat? f = null;
        while (reader.TryTake(out var frame))
        {
            using (frame)
            {
                f ??= PictureFormat.Of(frame, reader.TimeBase, rotation) ?? throw new InvalidOperationException(frame.PixelFormat);
                meter ??= new PictureMeter(f);
                meter.Add(frame);
            }
        }

        format = f!;
        return meter!.Measures;
    }

    static void Feed(string path, PictureMeter meter, int from)
    {
        using var reader = Reader(path);
        for (int i = 0; reader.TryTake(out var frame); i++)
        {
            using (frame)
            {
                if (i >= from)
                {
                    // Through the planes, as a decoder of the caller's own would hand them over.
                    var y = frame.GetPlane(0, out int ys);
                    var u = frame.GetPlane(1, out int us);
                    var v = frame.GetPlane(2, out int vs);
                    meter.Add(y, ys, u, us, v, vs, frame.Pts, frame.PictureType);
                }
            }
        }
    }

    static VideoFrameReader Reader(string path) => new(path, new() { Decoder = new() { Autorotate = false } });

    /// <summary>
    /// Whether <paramref name="value"/> is what FFmpeg printed as <paramref name="text"/> (%g, six significant digits;
    /// times to as many): within half a unit of its last digit.
    /// </summary>
    static bool Printed(string text, double value)
    {
        int e = text.IndexOfAny(['e', 'E']);
        string mantissa = e < 0 ? text : text[..e];
        int exponent = e < 0 ? 0 : int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
        int dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        int decimals = dot < 0 ? 0 : mantissa.Length - dot - 1;
        double unit = Math.Pow(10, exponent - decimals);
        return Math.Abs(double.Parse(text, CultureInfo.InvariantCulture) - value) <= unit / 2 * (1 + 1e-9);
    }

    sealed record Span(string Start, string End);

    sealed record FfmpegPicture(List<Dictionary<string, string>> Frames, string? Crop, List<Span> Black0, List<Span> Black2);

    /// <summary>ffprobe's pict_type of every frame, one character each.</summary>
    static string PictureTypes(string path) => string.Concat(Run("ffprobe",
        "-v",
        "error",
        "-select_streams",
        "v:0",
        "-show_entries",
        "frame=pict_type",
        "-of",
        "csv=p=0",
        path).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.TrimEnd(',')));

    /// <summary>
    /// One pass of the ffmpeg beside the libraries over the video with every filter: signalstats and the per-frame
    /// cropdetect printed by metadata=print, blackdetect at d=0 and at its default, cropdetect with its defaults in the
    /// log; -copyts keeps the stream's own timestamps; without autorotate, the frames as coded.
    /// </summary>
    static FfmpegPicture Filters(string path, bool autorotate = true)
    {
        string metadata = Path.Combine(Path.GetDirectoryName(path)!, "metadata.txt");
        string graph = $"signalstats=stat=brng,cropdetect=reset=1:skip=0,metadata=mode=print:file={Path.GetFileName(metadata)},"
            + "blackdetect=d=0,blackdetect,cropdetect";
        string log = Ffmpeg(["-hide_banner", "-nostats", "-nostdin", "-copyts", .. autorotate ? Array.Empty<string>() : ["-noautorotate"], "-i", path,
            "-map", "0:v:0", "-vf", graph, "-f", "null", "-"]);
        var frames = new List<Dictionary<string, string>>();
        foreach (string line in File.ReadLines(metadata))
        {
            if (line.StartsWith("frame:", StringComparison.Ordinal))
                frames.Add([]);
            else if (line.IndexOf('=', StringComparison.Ordinal) is int eq and > 0 && frames.Count > 0)
                frames[^1][line[..eq]] = line[(eq + 1)..];
        }

        string? crop = null;
        var black = new Dictionary<string, List<Span>>();
        foreach (string line in log.Split('\n'))
        {
            // [Parsed_blackdetect_3 @ 0x...] black_start:1.96 black_end:2.52 black_duration:0.56
            // [Parsed_cropdetect_5 @ 0x...] x1:0 x2:351 ... crop=352:288:0:0
            if (line.Contains("[Parsed_cropdetect_5 ", StringComparison.Ordinal))
                crop = line[line.LastIndexOf("crop=", StringComparison.Ordinal)..].Trim();
            int at = line.IndexOf("[Parsed_blackdetect_", StringComparison.Ordinal);
            if (at < 0 || !line.Contains("black_start:", StringComparison.Ordinal))
                continue;
            string filter = line[(at + 1)..line.IndexOf(' ', at)];
            var words = line[(line.IndexOf(']', at) + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Split(':'))
                .ToDictionary(w => w[0], w => w[1]);
            if (!black.TryGetValue(filter, out var list))
                black[filter] = list = [];
            list.Add(new Span(words["black_start"], words["black_end"]));
        }

        return new FfmpegPicture(frames, crop, black.GetValueOrDefault("Parsed_blackdetect_3") ?? [], black.GetValueOrDefault("Parsed_blackdetect_4") ?? []);
    }

    static string Ffmpeg(params string[] args) => Run("ffmpeg", args);

    static string Run(string program, params string[] args)
    {
        string? input = args.Contains("-copyts") ? args[Array.IndexOf(args, "-i") + 1] : null;
        var psi = new ProcessStartInfo(Path.Combine(Libs!, OperatingSystem.IsWindows() ? program + ".exe" : program))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = input is null ? null : Path.GetDirectoryName(input),
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err);
        return program == "ffprobe" ? stdout.Result : err + stdout.Result;
    }
}
