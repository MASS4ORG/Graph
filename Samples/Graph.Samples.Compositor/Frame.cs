namespace MASS4.Graph.Samples.Compositor;

/// <summary>A greyscale video frame; nodes reuse frames between evaluations, so frames compare by identity.</summary>
public sealed class Frame
{
    /// <summary>Creates a black frame.</summary>
    public Frame(int width, int height)
    {
        Width = width;
        Height = height;
        Luma = new float[width * height];
    }

    /// <summary>Pixels per row.</summary>
    public int Width { get; }

    /// <summary>Rows.</summary>
    public int Height { get; }

    /// <summary>Row-major luminance in [0, 1].</summary>
    public float[] Luma { get; }

    /// <summary>Mean luminance.</summary>
    public double Mean => Luma.Length == 0 ? 0 : Luma.Average();

    /// <summary>Returns <paramref name="existing"/> when it has the requested size, otherwise a new frame.</summary>
    public static Frame Reuse(Frame? existing, int width, int height) =>
        existing is not null && existing.Width == width && existing.Height == height ? existing : new(width, height);

    /// <summary>A content hash stable across runs and machines (FNV-1a over the pixel bits).</summary>
    public ulong Checksum()
    {
        var hash = 14695981039346656037UL;
        foreach (var value in Luma)
        {
            hash = (hash ^ BitConverter.SingleToUInt32Bits(value)) * 1099511628211UL;
        }
        return hash;
    }
}

/// <summary>Compares frames by size and pixels, for differential testing and tools.</summary>
public sealed class FrameComparer : IEqualityComparer<Frame>, System.Collections.IEqualityComparer
{
    /// <inheritdoc />
    public bool Equals(Frame? x, Frame? y) =>
        ReferenceEquals(x, y) ||
        (x is not null && y is not null && x.Width == y.Width && x.Height == y.Height &&
         x.Luma.AsSpan().SequenceEqual(y.Luma));

    /// <inheritdoc />
    public int GetHashCode(Frame frame) => (int)frame.Checksum();

    bool System.Collections.IEqualityComparer.Equals(object? x, object? y) => Equals(x as Frame, y as Frame);

    int System.Collections.IEqualityComparer.GetHashCode(object obj) => GetHashCode((Frame)obj);
}

/// <summary>Evaluation-context feature: the time on the timeline being rendered.</summary>
public sealed class Timeline
{
    /// <summary>Seconds from the start of the composition.</summary>
    public double Seconds { get; set; }
}

/// <summary>Evaluation-context feature: the size of rendered frames.</summary>
public sealed class RenderSettings
{
    /// <summary>Pixels per row.</summary>
    public int Width { get; set; } = 8;

    /// <summary>Rows.</summary>
    public int Height { get; set; } = 2;
}

/// <summary>Digest of one rendered frame.</summary>
/// <param name="Seconds">Timeline time of the frame.</param>
/// <param name="Mean">Mean luminance.</param>
/// <param name="Checksum">Content hash.</param>
public readonly record struct RenderedFrame(double Seconds, double Mean, ulong Checksum);
