using System.Globalization;

namespace Hubwright.Theming;

/// <summary>An sRGB colour, with the conversions and WCAG measures the theme designer needs.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);

    /// <summary>Parses <c>#0f6cbd</c>, <c>0f6cbd</c> or <c>#fff</c>.</summary>
    public static Rgb Parse(string hex)
    {
        if (!TryParse(hex, out var color))
        {
            throw new FormatException($"'{hex}' is not a colour. Use #rrggbb, for example #0f6cbd.");
        }

        return color;
    }

    public static bool TryParse(string? hex, out Rgb color)
    {
        color = default;
        var text = (hex ?? "").Trim().TrimStart('#');
        if (text.Length == 3)
        {
            text = string.Concat(text.Select(c => $"{c}{c}"));
        }

        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        color = new Rgb((byte)(value >> 16), (byte)(value >> 8 & 0xff), (byte)(value & 0xff));
        return true;
    }

    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    public override string ToString() => Hex;

    /// <summary>Moves toward <paramref name="other"/> by <paramref name="amount"/> (0 = this colour, 1 = the other).</summary>
    public Rgb Mix(Rgb other, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new Rgb(Lerp(R, other.R, amount), Lerp(G, other.G, amount), Lerp(B, other.B, amount));
    }

    private static byte Lerp(byte from, byte to, double amount) => (byte)Math.Round(from + ((to - from) * amount));

    /// <summary>WCAG 2.x relative luminance.</summary>
    public double Luminance => (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>WCAG 2.x contrast ratio, 1 to 21.</summary>
    public double ContrastWith(Rgb other)
    {
        var (light, dark) = Luminance >= other.Luminance ? (Luminance, other.Luminance) : (other.Luminance, Luminance);
        return (light + 0.05) / (dark + 0.05);
    }

    public bool IsDark => Luminance < 0.18;

    public (double H, double S, double L) ToHsl()
    {
        double r = R / 255.0, g = G / 255.0, b = B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max - min < 1e-9)
        {
            return (0, 0, l);
        }

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
        return (h * 60, s, l);
    }

    public static Rgb FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        if (s < 1e-9)
        {
            var gray = (byte)Math.Round(l * 255);
            return new Rgb(gray, gray, gray);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        return new Rgb(Hue(p, q, h + (1 / 3.0)), Hue(p, q, h), Hue(p, q, h - (1 / 3.0)));
    }

    private static byte Hue(double p, double q, double t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        var v = t < 1 / 6.0 ? p + ((q - p) * 6 * t)
            : t < 1 / 2.0 ? q
            : t < 2 / 3.0 ? p + ((q - p) * ((2 / 3.0) - t) * 6)
            : p;
        return (byte)Math.Round(v * 255);
    }

    /// <summary>Same hue and saturation, with the lightness moved by <paramref name="delta"/> (−1..1).</summary>
    public Rgb WithLightness(double delta)
    {
        var (h, s, l) = ToHsl();
        return FromHsl(h, s, l + delta);
    }
}
