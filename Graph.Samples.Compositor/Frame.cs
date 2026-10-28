namespace MASS4.Graph.Samples.Compositor;

/// <summary>
/// A greyscale video frame. Frames compare by content, so different backends producing the same image are equal.
/// </summary>
public sealed class Frame : IEquatable<Frame>
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

    /// <inheritdoc />
    public bool Equals(Frame? other) =>
        other is not null && other.Width == Width && other.Height == Height && other.Luma.AsSpan().SequenceEqual(Luma);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as Frame);

    /// <inheritdoc />
    public override int GetHashCode() => (int)Checksum();
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
