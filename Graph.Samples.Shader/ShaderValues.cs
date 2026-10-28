namespace MASS4.Graph.Samples.Shader;

/// <summary>A linear RGBA colour.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha.</param>
public readonly record struct Color(float R, float G, float B, float A)
{
    /// <summary>A grey with the same value in every channel, including alpha.</summary>
    public static Color Splat(float value) => new(value, value, value, value);

    /// <summary>Channel-wise product.</summary>
    public static Color operator *(Color a, Color b) => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);

    /// <summary>Scales every channel.</summary>
    public static Color operator *(Color a, float s) => new(a.R * s, a.G * s, a.B * s, a.A * s);

    /// <summary>Channel-wise sum.</summary>
    public static Color operator +(Color a, Color b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);

    /// <summary>Linear interpolation from <paramref name="a"/> (t = 0) to <paramref name="b"/> (t = 1).</summary>
    public static Color Lerp(Color a, Color b, float t) => a * (1 - t) + b * t;
}

/// <summary>Texture coordinates in [0, 1).</summary>
/// <param name="U">Horizontal coordinate.</param>
/// <param name="V">Vertical coordinate.</param>
public readonly record struct Uv(float U, float V);

/// <summary>An immutable texture sampled with nearest filtering and wrapping.</summary>
/// <param name="width">Texels per row.</param>
/// <param name="height">Rows.</param>
/// <param name="texels">Row-major texels.</param>
public sealed class Texture(int width, int height, Color[] texels)
{
    /// <summary>Returns the texel under the coordinates.</summary>
    public Color Sample(Uv uv)
    {
        var x = (int)MathF.Floor(uv.U * width) % width;
        var y = (int)MathF.Floor(uv.V * height) % height;
        return texels[(y + height) % height * width + (x + width) % width];
    }

    /// <summary>A two-by-two checkerboard of two colours.</summary>
    public static Texture Checker(Color a, Color b) => new(2, 2, [a, b, b, a]);
}

/// <summary>Evaluation-context feature carrying the fragment being shaded.</summary>
public sealed class Fragment
{
    /// <summary>Texture coordinates of the fragment.</summary>
    public Uv Uv { get; set; }
}
