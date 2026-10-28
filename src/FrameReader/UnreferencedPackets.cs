using FFmpeg.AutoGen;

namespace FrameReader;

/// <summary>
/// Which packets hold only frames no other frame references, so that a reader looking for a frame further on can leave
/// them out of the decoder without changing any other frame (<see cref="FrameDecoder.SkipBefore"/>): H.264 slices with
/// nal_ref_idc 0, HEVC sub-layer non-reference pictures of the highest temporal layer, MPEG-1/2 B-pictures and MPEG-4
/// Part 2 B-VOPs. A packet that also carries parameter sets (or headers) is kept. Off for good, from the packet that shows
/// it: field-coded H.264 (frame_mbs_only_flag 0, whose fields can come in packets of their own) or an SPS without a
/// reorder depth (FFmpeg then guesses it from the frames it sees, so leaving some out could change its output), MPEG-4
/// with DivX packed B-frames (the decoder brings a packed B-frame out with the next packet). Interlaced MPEG-2 and
/// field-ordered HEVC, VP9 and AV1 (whose non-reference frames can still update the probability contexts, or share a
/// packet with a reference frame) aren't looked at.
/// </summary>
internal sealed unsafe class UnreferencedPackets
{
    enum Codec
    {
        H264,
        Hevc,
        Mpeg12,
        Mpeg4,
    }

    readonly Codec _codec;
    readonly int _lengthSize; // ISO length-prefixed NAL units (avcC, hvcC): the length field's bytes; 0: Annex B start codes
    int _hevcTopLayer = -1; // HEVC: sps_max_sub_layers_minus1 of the SPS seen (the highest TemporalId); -1: none seen yet
    bool _h264Ready; // H.264: an SPS seen, frame coded with a reorder depth
    bool _afterIdr; // H.264: an IDR decoded since the start or the last seek
    bool _off;

    UnreferencedPackets(Codec codec, int lengthSize) => (_codec, _lengthSize) = (codec, lengthSize);

    /// <summary>No packet is unreferenced from here on (the stream showed something the rules don't cover).</summary>
    internal void TurnOff() => _off = true;

    /// <summary>
    /// After a seek: H.264 lets packets go again only from the next IDR. From a recovery point that isn't one (an I-frame
    /// with a recovery point SEI, where MPEG-TS seeks can land), FFmpeg holds back the frames it hasn't recovered yet by
    /// the order they come out in, which a left-out frame changes; after an IDR every frame counts as recovered.
    /// </summary>
    internal void Restart() => _afterIdr = false;

    /// <summary>For a stream's parameters; null when its codec (or field order) isn't one whose packets are looked at.</summary>
    internal static UnreferencedPackets? For(AVCodecParameters* par) =>
        For(par->codec_id, par->field_order, par->extradata == null ? [] : new ReadOnlySpan<byte>(par->extradata, par->extradata_size));

    /// <summary>For a codec, field order and the stream's extradata (avcC, hvcC, or headers with start codes).</summary>
    internal static UnreferencedPackets? For(AVCodecID codecId, AVFieldOrder fieldOrder, ReadOnlySpan<byte> extradata)
    {
        int n = extradata.Length;
        byte first = n > 0 ? extradata[0] : (byte)0;
        bool progressive = fieldOrder == AVFieldOrder.AV_FIELD_PROGRESSIVE, unknown = fieldOrder == AVFieldOrder.AV_FIELD_UNKNOWN;
        UnreferencedPackets? found = codecId switch
        {
            // H.264's own SPS says whether fields are coded. MPEG-1 and MPEG-4 Part 2 have no field pictures (MPEG-4's
            // interlace is within the frame), so their field order doesn't matter; HEVC's fields are pictures of their own.
            AVCodecID.AV_CODEC_ID_H264 => new(Codec.H264, n >= 7 && first == 1 ? (extradata[4] & 3) + 1 : 0),
            AVCodecID.AV_CODEC_ID_HEVC when progressive || unknown => new(Codec.Hevc, n >= 23 && first == 1 ? (extradata[21] & 3) + 1 : 0),
            AVCodecID.AV_CODEC_ID_MPEG1VIDEO => new(Codec.Mpeg12, 0),
            AVCodecID.AV_CODEC_ID_MPEG2VIDEO when progressive => new(Codec.Mpeg12, 0),
            AVCodecID.AV_CODEC_ID_MPEG4 => new(Codec.Mpeg4, 0),
            _ => null,
        };
        if (found is not null && n > 0)
        {
            fixed (byte* x = extradata)
                found.Extradata(x, n);
        }

        return found;
    }

    /// <summary>The parameter sets (or headers) in the stream's extradata.</summary>
    void Extradata(byte* x, int n)
    {
        switch (_codec)
        {
            case Codec.H264 when _lengthSize > 0:
                {
                    // avcC: 5 bytes, then the SPS count (low 5 bits) and each SPS with a 16-bit length.
                    int count = x[5] & 0x1f, i = 6;
                    for (int k = 0; k < count && i + 2 <= n; k++)
                    {
                        int len = (x[i] << 8) | x[i + 1];
                        i += 2;
                        if (i + len > n)
                            break;
                        Nal(x + i, len);
                        i += len;
                    }

                    break;
                }
            case Codec.Hevc when _lengthSize > 0:
                {
                    // hvcC: 22 bytes, then arrays of NAL units, each with its type, a count and 16-bit lengths.
                    int arrays = n > 22 ? x[22] : 0, i = 23;
                    for (int a = 0; a < arrays && i + 3 <= n; a++)
                    {
                        int count = (x[i + 1] << 8) | x[i + 2];
                        i += 3;
                        for (int k = 0; k < count && i + 2 <= n; k++)
                        {
                            int len = (x[i] << 8) | x[i + 1];
                            i += 2;
                            if (i + len > n)
                                return;
                            Nal(x + i, len);
                            i += len;
                        }
                    }

                    break;
                }
            case Codec.Mpeg4:
                _off |= DivXPacked(x, n);
                break;
            default:
                Units(x, n, 0);
                break;
        }
    }

    /// <summary>
    /// Whether the packet holds only frames nothing references (and nothing else that sets state). Keyframes and every
    /// packet that might be left out come through here, so the parameter sets and headers that matter are seen.
    /// </summary>
    internal bool Unreferenced(ReadOnlySpan<byte> packet)
    {
        if (_off || packet.IsEmpty)
            return false;
        bool unreferenced;
        fixed (byte* p = packet)
        {
            unreferenced = _codec switch
            {
                Codec.H264 or Codec.Hevc => Units(p, packet.Length, _lengthSize),
                Codec.Mpeg12 => Mpeg12(p, packet.Length),
                _ => Mpeg4(p, packet.Length),
            };
        }

        return unreferenced && !_off;
    }

    /// <summary>
    /// The NAL units of an H.264 or HEVC packet (length-prefixed or Annex B): true when there is a slice and every slice is
    /// of a frame nothing references, and nothing but SEI, delimiters and filler is with them.
    /// </summary>
    bool Units(byte* p, int size, int lengthSize)
    {
        int slices = 0;
        bool unreferenced = true;
        if (lengthSize > 0)
        {
            int i = 0;
            while (i + lengthSize <= size)
            {
                int len = 0;
                for (int k = 0; k < lengthSize; k++)
                    len = (len << 8) | p[i + k];
                i += lengthSize;
                if (len <= 0 || len > size - i)
                    return false;
                unreferenced &= Nal(p + i, len, ref slices);
                i += len;
            }

            return unreferenced && i == size && slices > 0;
        }

        int start = -1;
        for (int i = 0; i + 2 < size; i++)
        {
            if (p[i] != 0 || p[i + 1] != 0 || p[i + 2] != 1)
                continue;
            if (start >= 0)
                unreferenced &= Nal(p + start, i - start, ref slices);
            start = i + 3;
            i += 2;
        }

        if (start >= 0)
            unreferenced &= Nal(p + start, size - start, ref slices);
        return unreferenced && slices > 0;
    }

    void Nal(byte* nal, int len)
    {
        int slices = 0;
        Nal(nal, len, ref slices);
    }

    /// <summary>One NAL unit: false when it is a slice of a referenced frame or anything that sets state.</summary>
    bool Nal(byte* nal, int len, ref int slices)
    {
        if (len < 1)
            return true; // trailing zeros between start codes
        if (_codec == Codec.H264)
        {
            int type = nal[0] & 0x1f;
            switch (type)
            {
                case 1: // a non-IDR slice: unreferenced with nal_ref_idc 0, once the SPS allows it and after an IDR
                    slices++;
                    return (nal[0] & 0x60) == 0 && _h264Ready && _afterIdr;
                case 5:
                    _afterIdr = true;
                    return false;
                case 7:
                    Sps(nal, len);
                    return false;
                case 6 or 9 or 12: // SEI, access unit delimiter, filler
                    return true;
                default: // IDR, partitions, PPS, ...
                    return false;
            }
        }

        if (len < 2)
            return false;
        int hevcType = (nal[0] >> 1) & 0x3f, tid = (nal[1] & 7) - 1;
        if (hevcType <= 31)
        {
            // Sub-layer non-reference (even types up to RSV_VCL_N14) of the highest layer: no picture references it.
            slices++;
            return hevcType <= 14 && hevcType % 2 == 0 && _hevcTopLayer >= 0 && tid == _hevcTopLayer;
        }

        if (hevcType == 33 && len >= 3)
            _hevcTopLayer = Math.Max(_hevcTopLayer, (nal[2] >> 1) & 7); // sps_video_parameter_set_id (4), sps_max_sub_layers_minus1 (3)
        return hevcType is 35 or 38 or 39 or 40; // AUD, filler, SEI; VPS/SPS/PPS and the rest: kept
    }

    /// <summary>An H.264 SPS: frame-coded with a reorder depth (VUI bitstream_restriction) lets packets go; else off for good.</summary>
    void Sps(byte* nal, int len)
    {
        var b = new Bits(nal + 1, len - 1);
        bool ready = false;
        int profile = b.U(8);
        b.U(16); // constraint flags, level_idc
        b.Ue(); // seq_parameter_set_id
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            int chroma = b.Ue();
            if (chroma == 3)
                b.U(1); // separate_colour_plane_flag
            b.Ue(); // bit_depth_luma_minus8
            b.Ue(); // bit_depth_chroma_minus8
            b.U(1); // qpprime_y_zero_transform_bypass_flag

            // seq_scaling_matrix_present_flag
            if (b.U(1) == 1)
            {
                for (int i = 0; i < (chroma != 3 ? 8 : 12); i++)
                {
                    if (b.U(1) == 1)
                        ScalingList(ref b, i < 6 ? 16 : 64);
                }
            }
        }

        b.Ue(); // log2_max_frame_num_minus4
        int pocType = b.Ue();
        if (pocType == 0)
        {
            b.Ue(); // log2_max_pic_order_cnt_lsb_minus4
        }
        else if (pocType == 1)
        {
            b.U(1); // delta_pic_order_always_zero_flag
            b.Se(); // offset_for_non_ref_pic
            b.Se(); // offset_for_top_to_bottom_field
            int cycle = b.Ue();
            for (int i = 0; i < cycle; i++)
                b.Se();
        }

        b.Ue(); // max_num_ref_frames
        b.U(1); // gaps_in_frame_num_value_allowed_flag
        b.Ue(); // pic_width_in_mbs_minus1
        b.Ue(); // pic_height_in_map_units_minus1

        // frame_mbs_only_flag
        if (b.U(1) == 1)
        {
            b.U(1); // direct_8x8_inference_flag

            // frame_cropping_flag
            if (b.U(1) == 1)
            {
                for (int i = 0; i < 4; i++)
                    b.Ue();
            }

            ready = b.U(1) == 1 && Vui(ref b); // vui_parameters_present_flag
        }

        ready &= !b.Ended; // cut short: not understood
        _off |= !(_h264Ready = ready);
    }

    static void ScalingList(ref Bits b, int size)
    {
        int last = 8, next = 8;
        for (int j = 0; j < size && next != 0; j++)
        {
            next = (last + b.Se() + 256) % 256;
            last = next == 0 ? last : next;
        }
    }

    /// <summary>The VUI up to bitstream_restriction_flag and, when set, max_num_reorder_frames: true when it is there.</summary>
    static bool Vui(ref Bits b)
    {
        // aspect_ratio_info_present_flag, aspect_ratio_idc (Extended_SAR)
        if (b.U(1) == 1 && b.U(8) == 255)
            b.U(32);

        // overscan_info_present_flag
        if (b.U(1) == 1)
            b.U(1);

        // video_signal_type_present_flag
        if (b.U(1) == 1)
        {
            b.U(4);

            // colour_description_present_flag
            if (b.U(1) == 1)
                b.U(24);
        }

        // chroma_loc_info_present_flag
        if (b.U(1) == 1)
        {
            b.Ue();
            b.Ue();
        }

        // timing_info_present_flag
        if (b.U(1) == 1)
        {
            b.U(32);
            b.U(32);
            b.U(1);
        }

        bool nal = b.U(1) == 1;
        if (nal)
            Hrd(ref b);
        bool vcl = b.U(1) == 1;
        if (vcl)
            Hrd(ref b);
        if (nal || vcl)
            b.U(1); // low_delay_hrd_flag
        b.U(1); // pic_struct_present_flag
        return b.U(1) == 1; // bitstream_restriction_flag
    }

    static void Hrd(ref Bits b)
    {
        int count = b.Ue() + 1;
        b.U(8); // bit_rate_scale, cpb_size_scale
        for (int i = 0; i < count; i++)
        {
            b.Ue();
            b.Ue();
            b.U(1);
        }

        b.U(20); // four 5-bit lengths
    }

    /// <summary>MPEG-1/2: only B-pictures, no sequence or GOP header (a field-coded B-frame's two pictures count as one).</summary>
    static bool Mpeg12(byte* p, int size)
    {
        int pictures = 0;
        for (int i = 0; i + 5 < size; i++)
        {
            if (p[i] != 0 || p[i + 1] != 0 || p[i + 2] != 1)
                continue;
            byte code = p[i + 3];
            if (code == 0x00)
            {
                // picture_coding_type, after the 10-bit temporal_reference
                if (((p[i + 5] >> 3) & 7) != 3)
                    return false;
                pictures++;
            }
            else if (code is 0xB3 or 0xB7 or 0xB8)
            {
                return false; // sequence header, sequence end, GOP
            }

            i += 3;
        }

        return pictures > 0;
    }

    /// <summary>MPEG-4 Part 2: one B-VOP and no headers; DivX packed B-frames (user data "DivX...p") turn it off for good.</summary>
    bool Mpeg4(byte* p, int size)
    {
        if (_off |= DivXPacked(p, size))
            return false;
        int vops = 0;
        for (int i = 0; i + 4 < size; i++)
        {
            if (p[i] != 0 || p[i + 1] != 0 || p[i + 2] != 1)
                continue;
            byte code = p[i + 3];
            if (code == 0xB6)
            {
                // vop_coding_type: B
                if (((p[i + 4] >> 6) & 3) != 2)
                    return false;
                vops++;
            }
            else if (code <= 0x2F || code is 0xB0 or 0xB3 or 0xB5)
            {
                return false; // video object / object layer / sequence / GOV headers
            }

            i += 3;
        }

        return vops == 1;
    }

    /// <summary>User data saying DivX packed bitstream ("DivX503b1393p"), as FFmpeg's MPEG-4 decoder reads it.</summary>
    static bool DivXPacked(byte* p, int size)
    {
        for (int i = 0; i + 8 < size; i++)
        {
            if (p[i] != 0 || p[i + 1] != 0 || p[i + 2] != 1 || p[i + 3] != 0xB2 || p[i + 4] != 'D' || p[i + 5] != 'i' || p[i + 6] != 'v' || p[i + 7] != 'X')
                continue;
            for (int j = i + 8; j < size && p[j] != 0; j++)
            {
                if (p[j] == 'p')
                    return true;
            }
        }

        return false;
    }

    /// <summary>An RBSP's bits (emulation prevention bytes skipped), most significant first; zeros past the end (<see cref="Ended"/>).</summary>
    ref struct Bits
    {
        readonly byte* _p;
        readonly int _size;
        int _byte, _bit, _zeros;
        byte _current;

        internal Bits(byte* p, int size)
        {
            _p = p;
            (_size, _byte, _bit, _zeros, _current, Ended) = (size, -1, 8, 0, 0, false);
        }

        /// <summary>Whether a read went past the end (or an exp-Golomb code was longer than 32 bits).</summary>
        internal bool Ended { get; private set; }

        int Bit()
        {
            if (_bit == 8)
            {
                _byte++;
                if (_byte < _size && _zeros >= 2 && _p[_byte] == 3)
                {
                    // emulation_prevention_three_byte
                    _byte++;
                    _zeros = 0;
                }

                if (_byte >= _size)
                {
                    Ended = true;
                    return 0;
                }

                _current = _p[_byte];
                _zeros = _current == 0 ? _zeros + 1 : 0;
                _bit = 0;
            }

            return (_current >> (7 - _bit++)) & 1;
        }

        internal int U(int n)
        {
            long v = 0;
            for (int i = 0; i < n; i++)
                v = (v << 1) | (uint)Bit();
            return (int)v;
        }

        internal int Ue()
        {
            int zeros = 0;
            while (Bit() == 0)
            {
                if (++zeros > 31)
                {
                    Ended = true;
                    return 0;
                }
            }

            return (int)((1L << zeros) - 1 + U(zeros));
        }

        internal int Se()
        {
            int k = Ue();
            return (k & 1) == 1 ? (k + 1) / 2 : -(k / 2);
        }
    }
}
