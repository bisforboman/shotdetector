using System.Globalization;

/// <summary>
/// scenedetect's config file (scenedetect.cfg, read with -c): INI sections named after its commands, as Python's
/// configparser reads them (keys are case-insensitive, '#' and ';' start comment lines, "key = value" or "key: value").
/// </summary>
sealed class Config
{
    readonly Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public static Config Read(string path)
    {
        var config = new Config();
        Dictionary<string, string>? section = null;
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                string name = line[1..^1].Trim();
                if (!config._sections.TryGetValue(name, out section))
                    config._sections[name] = section = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int sep = line.IndexOfAny(['=', ':']);
            if (sep < 0 || section is null)
                throw new FormatException($"{path}: can't read the line '{raw}'.");
            section[line[..sep].Trim()] = line[(sep + 1)..].Trim();
        }
        return config;
    }

    public string? Get(string section, string key)
    {
        if (!_sections.TryGetValue(section, out var s) || !s.TryGetValue(key, out var v))
            return null;
        return v;
    }

    public double? Double(string section, string key) =>
        Get(section, key) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : null;

    public int? Int(string section, string key) =>
        Get(section, key) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;

    /// <summary>configparser's getboolean: 1/yes/true/on and 0/no/false/off.</summary>
    public bool? Bool(string section, string key) => Get(section, key)?.ToLowerInvariant() switch
    {
        null => null,
        "1" or "yes" or "true" or "on" => true,
        "0" or "no" or "false" or "off" => false,
        var v => throw new FormatException($"[{section}] {key}: '{v}' is not a boolean."),
    };

    /// <summary>A min-scene-len style value; 0 means "use the global one" as in scenedetect.</summary>
    public string? Time(string section, string key) => Get(section, key) is { } v && v.Trim() is not ("0" or "0s" or "0.0") ? v : null;

    /// <summary>Score weights: four numbers, separated by spaces or ",", "/", "(", ")".</summary>
    public (double, double, double, double)? Weights(string section, string key)
    {
        if (Get(section, key) is not { } v)
            return null;
        var n = v.Split(new[] { ' ', ',', '/', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        return n.Length == 4 ? (n[0], n[1], n[2], n[3]) : throw new FormatException($"[{section}] {key}: weights need four numbers.");
    }

    // Every key ShotDetector reads (when the matching detector or output is used).
    static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "global.min-scene-len", "global.drop-short-scenes", "global.merge-last-scene", "global.frame-skip", "global.downscale",
        "global.crop", "global.output", "global.backend", "global.default-detector",
        "detect-adaptive.threshold", "detect-adaptive.min-scene-len", "detect-adaptive.weights", "detect-adaptive.luma-only",
        "detect-adaptive.kernel-size", "detect-adaptive.min-content-val", "detect-adaptive.frame-window",
        "detect-content.threshold", "detect-content.min-scene-len", "detect-content.weights", "detect-content.luma-only",
        "detect-content.kernel-size", "detect-content.filter-mode",
        "detect-threshold.threshold", "detect-threshold.min-scene-len", "detect-threshold.fade-bias", "detect-threshold.add-last-scene",
        "detect-hist.threshold", "detect-hist.min-scene-len", "detect-hist.bins",
        "detect-hash.threshold", "detect-hash.min-scene-len", "detect-hash.size", "detect-hash.lowpass",
        "load-scenes.start-col-name", "list-scenes.skip-cuts", "list-scenes.quiet",
        "save-images.num-images", "save-images.frame-margin", "save-images.width", "save-images.height", "save-images.scale",
        "save-images.format", "save-images.filename",
        "split-video.copy", "split-video.high-quality", "split-video.rate-factor", "split-video.preset", "split-video.args",
        "split-video.filename", "save-html.no-images", "save-html.image-width", "save-html.image-height",
        "save-edl.title", "save-edl.reel", "save-edl.start-timecode", "save-fcp.format", "save-otio.name", "save-otio.audio",
        "save-qp.disable-shift",
    };

    /// <summary>Keys in the file that ShotDetector doesn't have (they're ignored).</summary>
    public IEnumerable<string> Unsupported() =>
        _sections.SelectMany(s => s.Value.Keys.Select(k => $"{s.Key}.{k}")).Where(k => !Supported.Contains(k)).Order();
}
