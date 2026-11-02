using Hubwright.Config;

namespace Hubwright.Config;

/// <summary>
/// Turns token counts into dollars for backends that report usage but not cost.
/// The claude CLI reports its own cost, so this is mainly for API backends.
/// </summary>
public sealed class ModelPricing
{
    /// <summary>Anthropic first-party rates, USD per million tokens (input, output).</summary>
    private static readonly Dictionary<string, ModelPrice> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5-1"] = Price(10m, 50m),
        ["claude-fable-5"] = Price(10m, 50m),
        ["claude-opus-5-5"] = Price(4m, 20m),
        ["claude-opus-5"] = Price(5m, 25m),
        ["claude-opus-4-8"] = Price(5m, 25m),
        ["claude-opus-4-7"] = Price(5m, 25m),
        ["claude-opus-4-6"] = Price(5m, 25m),
        ["claude-sonnet-5"] = Price(2m, 10m),
        ["claude-sonnet-4-6"] = Price(3m, 15m),
        ["claude-haiku-4-5"] = Price(1m, 5m),

    };

    private readonly Dictionary<string, ModelPrice> _prices;

    public ModelPricing(IReadOnlyDictionary<string, ModelPrice>? overrides = null)
    {
        _prices = new Dictionary<string, ModelPrice>(BuiltIn, StringComparer.OrdinalIgnoreCase);
        foreach (var (model, price) in overrides ?? new Dictionary<string, ModelPrice>())
        {
            _prices[model] = price;
        }
    }

    /// <summary>Null when the model is unknown: an unpriced call is reported, not guessed at.</summary>
    public decimal? Estimate(string? model, long inputTokens, long outputTokens)
    {
        if (Find(model) is not { } price)
        {
            return null;
        }

        return (inputTokens * price.InputPerMillion + outputTokens * price.OutputPerMillion) / 1_000_000m;
    }

    /// <summary>
    /// Exact match first, then the longest known id the model starts with, so a dated or
    /// suffixed deployment name ("claude-opus-5-eu") still prices as its family. A provider
    /// prefix ("anthropic/claude-opus-5") is ignored.
    /// </summary>
    private ModelPrice? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var name = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;

        if (_prices.TryGetValue(name, out var exact))
        {
            return exact;
        }

        return _prices
            .Where(p => name.StartsWith(p.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Key.Length)
            .Select(p => p.Value)
            .FirstOrDefault();
    }

    private static ModelPrice Price(decimal input, decimal output)
        => new() { InputPerMillion = input, OutputPerMillion = output };
}
