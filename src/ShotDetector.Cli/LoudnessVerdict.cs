using System.Globalization;
using System.Text.Json;

/// <summary>
/// --loudness: FFmpeg's ebur128 figures against limits (EBU R128's by default: -23 LUFS ±0.5 LU, true peak at most
/// -1 dBTP). Integrated loudness and true peak are judged; the loudness range and sample peak are reported.
/// docs/decisions/2026-10-31-loudness-verdict.md.
/// </summary>
sealed record LoudnessVerdict(FrameReader.LoudnessResult Result, double Target, double Tolerance, double TruePeakMax)
{
    /// <summary>Integrated loudness within the tolerance of the target (at the tolerance passes).</summary>
    public bool IntegratedPass => Math.Abs(Result.Integrated - Target) <= Tolerance;

    /// <summary>True peak at most the maximum.</summary>
    public bool TruePeakPass => Result.TruePeak <= TruePeakMax;

    public bool Pass => IntegratedPass && TruePeakPass;

    static string PassText(bool pass) => pass ? "pass" : "FAIL";

    // One decimal, as ebur128's Summary prints; digital silence's peaks as C prints them.
    static string Db(double v) => double.IsNegativeInfinity(v) ? "-inf" : v.ToString("0.0", CultureInfo.InvariantCulture);

    static string Number(double v) => v.ToString(CultureInfo.InvariantCulture);

    public IEnumerable<string> Lines()
    {
        var r = Result;
        yield return $"Integrated loudness: {Db(r.Integrated)} LUFS (target {Number(Target)} +/- {Number(Tolerance)} LU): {PassText(IntegratedPass)}";
        yield return $"Loudness range:      {Db(r.Range)} LU ({Db(r.RangeLow)} to {Db(r.RangeHigh)} LUFS)";
        yield return $"True peak:           {Db(r.TruePeak)} dBTP (max {Number(TruePeakMax)}): {PassText(TruePeakPass)}";
        yield return $"Sample peak:         {Db(r.SamplePeak)} dBFS";
        yield return Pass ? "Loudness: pass" : "Loudness: out of range";
    }

    // By hand with Utf8JsonWriter (no reflection, for Native AOT). Full precision; digital silence's peaks are null.
    public string Json()
    {
        var buffer = new MemoryStream();
        using (var j = new Utf8JsonWriter(buffer, new() { Indented = true }))
        {
            static void Value(Utf8JsonWriter j, string name, double v)
            {
                if (double.IsFinite(v))
                    j.WriteNumber(name, v);
                else
                    j.WriteNull(name);
            }

            var r = Result;
            j.WriteStartObject();
            Value(j, "integrated", r.Integrated);
            Value(j, "range", r.Range);
            Value(j, "rangeLow", r.RangeLow);
            Value(j, "rangeHigh", r.RangeHigh);
            Value(j, "truePeak", r.TruePeak);
            Value(j, "samplePeak", r.SamplePeak);
            j.WriteNumber("target", Target);
            j.WriteNumber("tolerance", Tolerance);
            j.WriteNumber("truePeakMax", TruePeakMax);
            j.WriteBoolean("integratedPass", IntegratedPass);
            j.WriteBoolean("truePeakPass", TruePeakPass);
            j.WriteBoolean("pass", Pass);
            j.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
