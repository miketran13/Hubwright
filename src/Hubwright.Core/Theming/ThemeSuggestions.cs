namespace Hubwright.Theming;

/// <summary>One theme the editor offers, and why.</summary>
public sealed record ThemeSuggestion(string Label, string Why, SharePointTheme Theme, Rgb Primary, Rgb Background);

/// <summary>A check on an uploaded logo: whether it passes, and the evidence (Microsoft's words, or a labelled tip).</summary>
public sealed record LogoCheck(bool Passes, string Message, string? Quote = null, string? Source = null);

/// <summary>
/// Theme ideas from the brand colour and the logo's colours, each made accessible (WCAG 2.1 AA), and
/// checks for a logo against Microsoft's header guidance. Pure: the desktop decodes the image and
/// passes pixels.
/// </summary>
public static class ThemeSuggestions
{
    public const string LogoSource = "https://support.microsoft.com/en-us/office/change-the-look-of-your-sharepoint-site-06bbadc3-6b04-4a60-9d14-894f6a170818";
    public const string LogoSizeQuote = "please pick one that fits within the range of 64 x 64 pixels to 192 x 64 pixels (width x height).";
    public const string ThumbnailQuote = "must be a square logo for default use in experiences that require a square aspect ratio";

    private static readonly Rgb Dark = new(0x1b, 0x1a, 0x19);

    public static IReadOnlyList<ThemeSuggestion> Suggest(string organization, Rgb brand, IReadOnlyList<Rgb> logoColors)
    {
        ArgumentNullException.ThrowIfNull(logoColors);
        var list = new List<ThemeSuggestion>();
        void Add(string label, string why, Rgb primary, Rgb background)
        {
            var accessible = ThemeGenerator.SuggestAccessiblePrimary(primary, background) ?? primary;
            if (list.Any(s => Distance(s.Primary, accessible) < 24 && s.Background.Equals(background)))
            {
                return;
            }

            var theme = ThemeGenerator.Generate(new ThemeInput($"{organization} {label.ToLowerInvariant()}", accessible)
            {
                Background = background,
                Text = background.IsDark ? new Rgb(0xf3, 0xf2, 0xf1) : new Rgb(0x32, 0x31, 0x30),
            });
            var note = accessible.Equals(primary) ? "" : $" Adjusted from {primary.Hex} to {accessible.Hex} so text and buttons pass contrast.";
            list.Add(new ThemeSuggestion(label, why + note, theme, accessible, background));
        }

        Add("Brand", "Your brand colour as the primary colour on white.", brand, Rgb.White);
        foreach (var (color, i) in logoColors.Take(3).Select((c, i) => (c, i)))
        {
            Add(i == 0 ? "Logo" : $"Logo accent {i}", i == 0 ? "The strongest colour in your logo." : "Another colour from your logo.", color, Rgb.White);
        }

        var (h, s, l) = brand.ToHsl();
        Add("Calm", "A quieter, less saturated version of the brand for text-heavy intranets.", Rgb.FromHsl(h, Math.Min(s, 0.35), Math.Min(l, 0.4)), Rgb.White);
        Add("Dark", "Dark background with a lighter brand colour, for people who prefer dark mode.", Rgb.FromHsl(h, Math.Max(s, 0.5), Math.Max(l, 0.65)), Dark);
        return list;
    }

    /// <summary>
    /// The logo's main colours: opaque, not near white, black or grey, most frequent first. Takes
    /// 32-bit BGRA pixels, as Windows' decoder gives them.
    /// </summary>
    public static IReadOnlyList<Rgb> LogoColors(ReadOnlySpan<byte> bgra, int width, int height, int max = 4)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return [];
        }

        var counts = new Dictionary<int, (long R, long G, long B, int N)>();
        var step = Math.Max(1, (int)Math.Sqrt(width * height / 20000.0));
        for (var y = 0; y < height; y += step)
        {
            for (var x = 0; x < width; x += step)
            {
                var i = ((y * width) + x) * 4;
                byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];
                if (a < 128)
                {
                    continue;
                }

                var color = new Rgb(r, g, b);
                var (_, sat, light) = color.ToHsl();
                if (light > 0.92 || light < 0.08 || sat < 0.18)
                {
                    continue;
                }

                var bucket = (r >> 5 << 6) | (g >> 5 << 3) | (b >> 5);
                var c = counts.GetValueOrDefault(bucket);
                counts[bucket] = (c.R + r, c.G + g, c.B + b, c.N + 1);
            }
        }

        var colors = new List<Rgb>();
        foreach (var c in counts.Values.OrderByDescending(v => v.N))
        {
            var avg = new Rgb((byte)(c.R / c.N), (byte)(c.G / c.N), (byte)(c.B / c.N));
            if (colors.All(o => Distance(o, avg) > 60))
            {
                colors.Add(avg);
            }

            if (colors.Count == max)
            {
                break;
            }
        }

        return colors;
    }

    /// <summary>Checks a logo's shape and size against Microsoft's header guidance, plus labelled tips.</summary>
    public static IReadOnlyList<LogoCheck> CheckLogo(int width, int height, long bytes, bool hasTransparency, string extension)
    {
        var checks = new List<LogoCheck>();
        if (width <= 0 || height <= 0)
        {
            checks.Add(new LogoCheck(true, $"{extension.ToUpperInvariant().TrimStart('.')} logo: Hubwright can't measure it; check it looks right in the preview."));
            return checks;
        }

        var ratio = width / (double)height;
        checks.Add(ratio is >= 1 and <= 3
            ? new LogoCheck(true, $"Shape {width} x {height} fits the header (between square and 3:1).", LogoSizeQuote, LogoSource)
            : new LogoCheck(false, ratio < 1 ? $"Taller than wide ({width} x {height}): it will look small in the header. Use a version between square and 3:1." : $"Very wide ({width} x {height}): SharePoint will shrink it. Use a version no wider than 3:1.", LogoSizeQuote, LogoSource));
        checks.Add(height >= 64
            ? new LogoCheck(true, $"Tall enough ({height} px) to stay sharp at the header's 64 px.", LogoSizeQuote, LogoSource)
            : new LogoCheck(false, $"Only {height} px tall: it may look blurry. Export it at least 64 px tall.", LogoSizeQuote, LogoSource));
        checks.Add(width == height
            ? new LogoCheck(true, "Square: it also works as the site logo thumbnail.", ThumbnailQuote, LogoSource)
            : new LogoCheck(false, "Not square: also prepare a square version (just the symbol) for the site logo thumbnail.", ThumbnailQuote, LogoSource));
        checks.Add(hasTransparency
            ? new LogoCheck(true, "Transparent background: it sits cleanly on any header colour. (Hubwright tip)")
            : new LogoCheck(false, "No transparent background: on a coloured header it shows as a box. A PNG or SVG with transparency works better. (Hubwright tip)"));
        if (bytes > 1_000_000)
        {
            checks.Add(new LogoCheck(false, $"{bytes / 1024} KB is large for a logo; under about 200 KB loads faster. (Hubwright tip)"));
        }

        return checks;
    }

    private static double Distance(Rgb a, Rgb b)
        => Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));
}
