using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hubwright.Theming;

/// <summary>What the designer starts from: a brand colour, and optionally the page and text colours.</summary>
public sealed record ThemeInput(string Name, Rgb Primary)
{
    public Rgb Background { get; init; } = Rgb.White;

    public Rgb Text { get; init; } = new(0x32, 0x31, 0x30);
}

/// <summary>One text/background pair checked against WCAG 2.1 (1.4.3 Contrast, Minimum).</summary>
public sealed record ContrastCheck(string Usage, string Foreground, string Background, double Ratio, bool LargeTextOnly)
{
    /// <summary>4.5:1 for normal text, 3:1 for large text (18pt, or 14pt bold).</summary>
    public double Required => LargeTextOnly ? 3.0 : 4.5;

    public bool Passes => Ratio + 1e-9 >= Required;
}

/// <summary>A SharePoint theme: the palette Add-SPOTheme / Add-PnPTenantTheme take, plus its checks.</summary>
public sealed class SharePointTheme
{
    public required string Name { get; init; }

    public bool IsInverted { get; init; }

    /// <summary>The classic slot names (themePrimary … white), in the order the theme JSON uses.</summary>
    public required IReadOnlyDictionary<string, string> Palette { get; init; }

    public required IReadOnlyList<ContrastCheck> Checks { get; init; }

    public bool Accessible => Checks.All(c => c.Passes);

    public string ToJson() => JsonSerializer.Serialize(new { name = Name, isInverted = IsInverted, palette = Palette }, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>PowerShell that publishes the theme with either module, and optionally hides the default themes.</summary>
    public string ToPowerShell(bool hideDefaultThemes)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Hubwright theme \"{Name}\". Review, then run as a SharePoint Administrator.");
        sb.AppendLine("$palette = @{");
        foreach (var (slot, value) in Palette)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    \"{slot}\" = \"{value}\"");
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# SharePoint Online Management Shell:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Add-SPOTheme -Identity \"{Name}\" -Palette $palette -IsInverted ${(IsInverted ? "true" : "false")} -Overwrite");
        if (hideDefaultThemes)
        {
            sb.AppendLine("Set-SPOHideDefaultThemes $true");
        }

        sb.AppendLine();
        sb.AppendLine("# Or PnP PowerShell:");
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Add-PnPTenantTheme -Identity \"{Name}\" -Palette $palette -IsInverted ${(IsInverted ? "true" : "false")} -Overwrite");
        return sb.ToString();
    }
}

/// <summary>
/// Builds a SharePoint palette from a brand colour. Tints and shades are mixed toward the background
/// and black at fixed steps calibrated against the Fluent default palette for #0078d4, so the result
/// looks like what the Fluent theme designer produces; it is not a byte-for-byte copy of that tool.
/// For the official tool's exact output, paste its JSON into <see cref="FromJson"/>; the checks run
/// the same way.
/// </summary>
public static class ThemeGenerator
{
    private static readonly (string Slot, double Toward)[] Tints =
    [
        ("themeLighterAlt", 0.935), ("themeLighter", 0.863), ("themeLight", 0.77), ("themeTertiary", 0.42), ("themeSecondary", 0.13),
    ];

    private static readonly (string Slot, double Keep)[] Shades = [("themeDarkAlt", 0.9), ("themeDark", 0.75), ("themeDarker", 0.57)];

    // Share of the way from background to text, from the Fluent default neutrals (#faf9f8 … #323130).
    private static readonly (string Slot, double Toward)[] Neutrals =
    [
        ("neutralLighterAlt", 0.024), ("neutralLighter", 0.059), ("neutralLight", 0.093), ("neutralQuaternaryAlt", 0.151),
        ("neutralQuaternary", 0.229), ("neutralTertiaryAlt", 0.273), ("neutralTertiary", 0.463), ("neutralSecondary", 0.78),
        ("neutralPrimaryAlt", 0.956),
    ];

    public static SharePointTheme Generate(ThemeInput input)
    {
        var inverted = input.Background.IsDark;
        var palette = new Dictionary<string, string>(StringComparer.Ordinal) { ["themePrimary"] = input.Primary.Hex };
        foreach (var (slot, toward) in Tints)
        {
            palette[slot] = input.Primary.Mix(input.Background, toward).Hex;
        }

        foreach (var (slot, keep) in Shades)
        {
            palette[slot] = input.Primary.Mix(inverted ? Rgb.White : Rgb.Black, 1 - keep).Hex;
        }

        foreach (var (slot, toward) in Neutrals)
        {
            palette[slot] = input.Background.Mix(input.Text, toward).Hex;
        }

        palette["neutralPrimary"] = input.Text.Hex;
        palette["neutralDark"] = input.Text.Mix(inverted ? Rgb.White : Rgb.Black, 0.35).Hex;
        palette["black"] = input.Text.Mix(inverted ? Rgb.White : Rgb.Black, 0.7).Hex;
        palette["white"] = input.Background.Hex;

        return new SharePointTheme { Name = input.Name, IsInverted = inverted, Palette = Order(palette), Checks = Check(palette) };
    }

    /// <summary>Reads a theme JSON (Hubwright's, the Fluent designer's, or a bare palette object) and checks it.</summary>
    public static SharePointTheme FromJson(string json, string fallbackName = "Imported theme")
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var paletteElement = root.TryGetProperty("palette", out var nested) ? nested : root;
        var palette = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in paletteElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && Rgb.TryParse(property.Value.GetString(), out var color))
            {
                palette[property.Name] = color.Hex;
            }
        }

        if (!palette.ContainsKey("themePrimary"))
        {
            throw new FormatException("The theme has no themePrimary colour.");
        }

        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : fallbackName;
        var inverted = root.TryGetProperty("isInverted", out var i) && i.ValueKind == JsonValueKind.True;
        return new SharePointTheme { Name = name, IsInverted = inverted, Palette = Order(palette), Checks = Check(palette) };
    }

    /// <summary>
    /// The pairs SharePoint renders as text: body text, secondary text, links and buttons (primary on
    /// the page), button labels (white on primary) and hover links (themeDarkAlt).
    /// </summary>
    public static IReadOnlyList<ContrastCheck> Check(IReadOnlyDictionary<string, string> palette)
    {
        Rgb Get(string slot, Rgb fallback) => palette.TryGetValue(slot, out var hex) && Rgb.TryParse(hex, out var c) ? c : fallback;
        var page = Get("white", Rgb.White);
        var text = Get("neutralPrimary", new Rgb(0x32, 0x31, 0x30));
        var primary = Get("themePrimary", Rgb.Black);
        var secondary = Get("neutralSecondary", text);
        var hover = Get("themeDarkAlt", primary);
        var buttonText = page.IsDark ? Rgb.Black : Rgb.White;

        return
        [
            Pair("Body text on the page", text, page, large: false),
            Pair("Secondary text (dates, bylines)", secondary, page, large: false),
            Pair("Links and accent text", primary, page, large: false),
            Pair("Hovered links", hover, page, large: false),
            Pair("Button label on the brand colour", buttonText, primary, large: false),
            Pair("Large headings in the brand colour", primary, page, large: true),
        ];
    }

    private static ContrastCheck Pair(string usage, Rgb foreground, Rgb background, bool large)
        => new(usage, foreground.Hex, background.Hex, Math.Round(foreground.ContrastWith(background), 2), large);

    /// <summary>
    /// The nearest colour to <paramref name="primary"/> (same hue, lightness moved in small steps) that
    /// passes 4.5:1 both as text on the page and behind a button label. Null when no lightness does.
    /// </summary>
    public static Rgb? SuggestAccessiblePrimary(Rgb primary, Rgb background)
    {
        var label = background.IsDark ? Rgb.Black : Rgb.White;
        bool Ok(Rgb c) => c.ContrastWith(background) >= 4.5 && c.ContrastWith(label) >= 4.5;
        if (Ok(primary))
        {
            return primary;
        }

        for (var step = 0.01; step <= 1; step += 0.01)
        {
            foreach (var candidate in new[] { primary.WithLightness(background.IsDark ? step : -step), primary.WithLightness(background.IsDark ? -step : step) })
            {
                if (Ok(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static readonly string[] SlotOrder =
    [
        "themePrimary", "themeLighterAlt", "themeLighter", "themeLight", "themeTertiary", "themeSecondary", "themeDarkAlt", "themeDark", "themeDarker",
        "neutralLighterAlt", "neutralLighter", "neutralLight", "neutralQuaternaryAlt", "neutralQuaternary", "neutralTertiaryAlt", "neutralTertiary",
        "neutralSecondary", "neutralPrimaryAlt", "neutralPrimary", "neutralDark", "black", "white",
    ];

    private static Dictionary<string, string> Order(Dictionary<string, string> palette)
    {
        var ordered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var slot in SlotOrder.Where(palette.ContainsKey))
        {
            ordered[slot] = palette[slot];
        }

        foreach (var (slot, value) in palette.Where(p => !ordered.ContainsKey(p.Key)))
        {
            ordered[slot] = value;
        }

        return ordered;
    }
}
