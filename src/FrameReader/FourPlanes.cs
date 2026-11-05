namespace FrameReader;

/// <summary>Up to four planes of a frame and their strides (bytes from row to row), as the detectors take them.</summary>
internal ref struct FourPlanes
{
    ReadOnlySpan<byte> _p0, _p1, _p2, _p3;
    int _s0, _s1, _s2, _s3;

    public void Set(int i, ReadOnlySpan<byte> plane, int stride)
    {
        switch (i)
        {
            case 0:
                _p0 = plane;
                _s0 = stride;
                break;
            case 1:
                _p1 = plane;
                _s1 = stride;
                break;
            case 2:
                _p2 = plane;
                _s2 = stride;
                break;
            default:
                _p3 = plane;
                _s3 = stride;
                break;
        }
    }

    public readonly ReadOnlySpan<byte> Plane(int i) => i switch { 0 => _p0, 1 => _p1, 2 => _p2, _ => _p3 };

    public readonly int Stride(int i) => i switch { 0 => _s0, 1 => _s1, 2 => _s2, _ => _s3 };
}
