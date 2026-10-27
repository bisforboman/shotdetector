using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>Opening an input as ffprobe and ffmpeg do.</summary>
internal static unsafe class Demuxer
{
    /// <summary>
    /// The trusted certificates https checks against on Linux: the system's bundle, which FrameReader.Native's OpenSSL
    /// (looking in /etc/ssl, as Debian, Ubuntu and Alpine have it) wouldn't find on RHEL, Fedora or openSUSE. Null
    /// elsewhere (the system's store), when SSL_CERT_FILE names one (OpenSSL reads it), or when no bundle is there.
    /// </summary>
    internal static readonly string? CaBundle = OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSL_CERT_FILE"))
        ? Array.Find(["/etc/ssl/certs/ca-certificates.crt", "/etc/pki/tls/certs/ca-bundle.crt", "/etc/ssl/ca-bundle.pem", "/etc/ssl/cert.pem"], File.Exists)
        : null;

    /// <summary>
    /// After av_read_frame failed: the end of the input, unless reading it failed (a network reset, a Stream that threw:
    /// FFmpeg's I/O context records those), which is a <see cref="FrameReaderException"/> rather than a shortened
    /// result. A demuxer's own error (a truncated or damaged file) still ends the input, as in OpenCV.
    /// </summary>
    /// <param name="fmt">The input.</param>
    /// <param name="ret">What av_read_frame returned.</param>
    /// <param name="inner">What a Stream input threw, if it did.</param>
    internal static void ThrowIfReadFailed(AVFormatContext* fmt, int ret, Exception? inner)
    {
        if (ret == ffmpeg.AVERROR_EOF || fmt->pb == null || fmt->pb->error >= 0)
            return;
        if (fmt->pb->error == ffmpeg.AVERROR_EXIT)
            throw new OperationCanceledException(); // the interrupt callback: the read was cancelled
        throw new FrameReaderException(FrameReaderError.DecodeFailed,
            $"Reading the input failed part way: {inner?.Message ?? FFmpegLibraries.ErrorMessage(fmt->pb->error)}",
            inner);
    }

    /// <summary>
    /// Opens <paramref name="path"/> (or <paramref name="io"/>'s stream) as ffprobe and ffmpeg do (scan_all_pmts for
    /// MPEG-TS), with input options, and reads its stream info. Close with <see cref="Close"/> (then dispose io).
    /// <paramref name="cancellationToken"/> also stops FFmpeg where it waits (a network read): its interrupt callback.
    /// </summary>
    internal static AVFormatContext* Open(string path,
        IReadOnlyDictionary<string, string>? inputOptions = null,
        StreamInput? io = null,
        CancellationToken cancellationToken = default)
    {
        AVFormatContext* fmt = ffmpeg.avformat_alloc_context();
        if (io != null)
        {
            // Read from a Stream: avformat_close_input leaves the context to its owner.
            fmt->pb = io.Context;
            fmt->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
        }

        GCHandle cancel = default;
        if (cancellationToken.CanBeCanceled)
        {
            cancel = GCHandle.Alloc(cancellationToken);
            fmt->interrupt_callback.callback = Interrupted;
            fmt->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(cancel);
        }

        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "scan_all_pmts", "1", ffmpeg.AV_DICT_DONT_OVERWRITE);

        // https checks the server's certificate (FFmpeg 8 doesn't by default); tls_verify=0 in InputOptions turns it off.
        ffmpeg.av_dict_set(&options, "tls_verify", "1", ffmpeg.AV_DICT_DONT_OVERWRITE);
        if (CaBundle is { } bundle)
            ffmpeg.av_dict_set(&options, "ca_file", bundle, ffmpeg.AV_DICT_DONT_OVERWRITE);
        foreach (var (name, value) in inputOptions ?? new Dictionary<string, string>())
            ffmpeg.av_dict_set(&options, name, value, 0);

        // rtsp's listen mode waits forever by default (listen_timeout -1 s), handed to tcp as -1000 ms, which FFmpeg 8.1
        // tcp's option check refuses ("Numerical result out of range"): the input never opens. 0 is the same wait
        // without end (tcp's poll stops only on the interrupt callback), so it stands in when the caller leaves it out.
        bool listenTimeout = inputOptions != null && RtspListens(inputOptions)
            && !inputOptions.Keys.Any(k => string.Equals(k, "listen_timeout", StringComparison.OrdinalIgnoreCase));
        if (listenTimeout)
            ffmpeg.av_dict_set(&options, "listen_timeout", "0", 0);

        // The URLs the input opens itself (an HLS playlist's segments, keys and variants) are checked too, unless the
        // caller turned the check off or named their own certificates (tls_verify=0 or ca_file in InputOptions, which
        // FFmpeg wouldn't pass on to them either). tls_verify=1 from the caller still gets the wrapper.
        if (inputOptions is null || !inputOptions.Any(o => (o.Key is "tls_verify" or "verify" && o.Value == "0") || o.Key is "ca_file" or "cafile"))
        {
            _defaultIoOpen = fmt->io_open;
            fmt->io_open = NestedOpen;
        }

        int ret = ffmpeg.avformat_open_input(&fmt, path, null, &options);
        string? unused = ret >= 0 ? UnusedOption(options, listenTimeout) : null;
        ffmpeg.av_dict_free(&options);
        if (ret < 0)
        {
            if (cancel.IsAllocated)
                cancel.Free(); // avformat_open_input freed the context
            cancellationToken.ThrowIfCancellationRequested();
            throw new FrameReaderException(FrameReaderError.InvalidInput, $"FFmpeg (open {path}): {FFmpegLibraries.ErrorMessage(ret)}");
        }

        if (unused != null)
        {
            string format = Marshal.PtrToStringAnsi((nint)fmt->iformat->name) ?? "?";
            Close(&fmt);
            throw new ArgumentException($"No demuxer or protocol option \"{unused}\" (FFmpeg opened {path} as {format}).", "options");
        }

        ret = ffmpeg.avformat_find_stream_info(fmt, null);
        if (ret < 0)
        {
            Close(&fmt);
            cancellationToken.ThrowIfCancellationRequested();
            throw new FrameReaderException(FrameReaderError.InvalidInput, $"FFmpeg (read stream info): {FFmpegLibraries.ErrorMessage(ret)}");
        }

        // The interrupt callback can end find_stream_info early with success (what it found so far): a cancelled open
        // is cancelled, not a probe of the first bytes.
        if (cancellationToken.IsCancellationRequested)
        {
            Close(&fmt);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return fmt;
    }

    /// <summary>
    /// The first name in what avformat_open_input left of the options that the demuxer, the protocol and the format
    /// context all didn't take: a misspelt or unknown InputOptions name, which ffmpeg's command line refuses too. The
    /// names FrameReader sets on every input (scan_all_pmts, used by MPEG-TS only; tls_verify and ca_file, by TLS only)
    /// and their aliases are left over on most inputs, also when the caller sets them (tls_verify=0 for a plain file,
    /// or for an http HLS playlist whose segments are https), so they never count. Nor does the listen_timeout Open
    /// added (<paramref name="ownListenTimeout"/>): with rtsp_flags on any other input, rtsp_flags is the name reported.
    /// </summary>
    static string? UnusedOption(AVDictionary* options, bool ownListenTimeout)
    {
        AVDictionaryEntry* entry = null;
        while ((entry = ffmpeg.av_dict_get(options, "", entry, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
        {
            string key = Marshal.PtrToStringUTF8((nint)entry->key) ?? "";
            if (!OwnOptions.Contains(key) && !(ownListenTimeout && string.Equals(key, "listen_timeout", StringComparison.OrdinalIgnoreCase)))
                return key;
        }

        return null;
    }

    /// <summary>
    /// Whether InputOptions' rtsp_flags turn on listen, read as FFmpeg reads a flags value: names joined by + or -, a
    /// leading one without a sign replacing the default (none), each + adding and - removing its flag.
    /// </summary>
    internal static bool RtspListens(IReadOnlyDictionary<string, string> inputOptions)
    {
        var flags = inputOptions.FirstOrDefault(o => string.Equals(o.Key, "rtsp_flags", StringComparison.OrdinalIgnoreCase)).Value;
        if (flags is null)
            return false;
        bool listen = false;
        int i = 0;
        while (i < flags.Length)
        {
            char sign = flags[i] is '+' or '-' ? flags[i++] : '+';
            int end = flags.IndexOfAny(['+', '-'], i);
            if (end < 0)
                end = flags.Length;
            if (flags.AsSpan(i, end - i).Trim().Equals("listen", StringComparison.Ordinal))
                listen = sign == '+';
            i = end;
        }

        return listen;
    }

    // FrameReader's defaults (Open) and the TLS options' aliases; av_dict matches names ignoring case.
    static readonly HashSet<string> OwnOptions = new(["scan_all_pmts", "tls_verify", "verify", "ca_file", "cafile"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Closes an input <see cref="Open"/> opened, with what its interrupt callback held.</summary>
    internal static void Close(AVFormatContext** fmt)
    {
        if (*fmt == null)
            return;
        nint cancel = (nint)(*fmt)->interrupt_callback.opaque;
        ffmpeg.avformat_close_input(fmt);
        if (cancel != 0)
            GCHandle.FromIntPtr(cancel).Free();
    }

    // FFmpeg's own io_open (the same for every context), which NestedOpen calls.
    static AVFormatContext_io_open_func _defaultIoOpen;

    // Every URL the input opens after the first: FFmpeg's demuxers pass on only a fixed set of options to them
    // (ffio_copy_url_options: headers, cookies, rw_timeout...), without tls_verify, so https segments went unchecked.
    // Static, so it's never collected.
    static readonly AVFormatContext_io_open NestedOpen = (s, pb, url, flags, options) =>
    {
        AVDictionary* local = null;
        AVDictionary** opts = options != null ? options : &local;
        ffmpeg.av_dict_set(opts, "tls_verify", "1", ffmpeg.AV_DICT_DONT_OVERWRITE);
        if (CaBundle is { } bundle)
            ffmpeg.av_dict_set(opts, "ca_file", bundle, ffmpeg.AV_DICT_DONT_OVERWRITE);
        nint utf8 = Marshal.StringToCoTaskMemUTF8(url);   // FFmpeg.AutoGen gave it as a string
        try
        {
            return ((delegate* unmanaged[Cdecl]<AVFormatContext*, AVIOContext**, byte*, int, AVDictionary**, int>)_defaultIoOpen.Pointer)(s, pb, (byte*)utf8, flags, opts);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
            ffmpeg.av_dict_free(&local);
        }
    };

    // FFmpeg asks this while it waits; non-zero stops the wait (AVERROR_EXIT). Static, so it's never collected.
    static readonly AVIOInterruptCB_callback Interrupted = opaque =>
        GCHandle.FromIntPtr((nint)opaque).Target is CancellationToken { IsCancellationRequested: true } ? 1 : 0;

    /// <summary>The first video stream's index, as -select_streams v:0 / -map 0:v:0 take it; -1 if none.</summary>
    internal static int FirstVideoStream(AVFormatContext* fmt)
    {
        for (int i = 0; i < fmt->nb_streams; i++)
        {
            if (fmt->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                return i;
        }

        return -1;
    }

    /// <summary>
    /// Where ffmpeg's <c>-ss</c> seeks for <paramref name="timestamp"/> (AV_TIME_BASE, start time included): 3/23 s
    /// earlier for formats that seek by dts when any stream (video, even when reading audio) has B-frames.
    /// </summary>
    internal static long SeekPoint(AVFormatContext* fmt, long timestamp)
    {
        if ((fmt->iformat->flags & ffmpeg.AVFMT_SEEK_TO_PTS) == 0)
        {
            for (int i = 0; i < fmt->nb_streams; i++)
            {
                if (fmt->streams[i]->codecpar->video_delay > 0)
                    return timestamp - 3 * ffmpeg.AV_TIME_BASE / 23;
            }
        }

        return timestamp;
    }
}
