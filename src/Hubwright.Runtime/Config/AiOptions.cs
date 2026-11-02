namespace Hubwright.Config;

/// <summary>Where a model call goes. The CLIs bill a subscription login; the rest bill an API account.</summary>
public enum ModelBackend { Claude, Copilot, Anthropic, Foundry, GitHubModels, Local }

/// <summary>
/// How much model a step needs. <see cref="Default"/> is the backend's configured model; a light
/// model reads routine evidence cheaply, a heavy one makes the decisions that cost most when wrong.
/// </summary>
public enum ModelTier { Default, Light, Heavy }

/// <summary>The model backends and what they cost, as configured in <c>hubwright.json</c> under <c>ai</c>.</summary>
public sealed class AiOptions
{
    /// <summary>The backend every role uses unless <see cref="Roles"/> names another.</summary>
    public ModelBackend Backend { get; set; } = ModelBackend.Claude;

    /// <summary>Per-role backend, by role id (assessor, architect, brand, governance, assistant).</summary>
    public Dictionary<string, ModelBackend> Roles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The claude CLI's model: an alias (opus, sonnet, haiku) or a full name. Empty: the CLI's default.</summary>
    public string? ClaudeModel { get; set; }

    /// <summary>The copilot CLI's model. Empty: the CLI's default.</summary>
    public string? CopilotModel { get; set; }

    /// <summary>
    /// How hard the CLIs think: low, medium, high, xhigh or max (claude <c>--effort</c>, copilot
    /// <c>--reasoning-effort</c>). Empty: the CLI's default. The API backends ignore it.
    /// </summary>
    public string? Effort { get; set; }

    /// <summary>Added to every agent's system prompt: house rules that are not in the knowledge files.</summary>
    public string? Instructions { get; set; }

    public AnthropicOptions Anthropic { get; set; } = new();

    public FoundryOptions Foundry { get; set; } = new();

    public GitHubModelsOptions GitHubModels { get; set; } = new();

    public LocalModelOptions Local { get; set; } = new();

    public BudgetOptions Budget { get; set; } = new();

    /// <summary>The light and heavy models per backend, for steps that choose their tier (the backtest lab).</summary>
    public ModelTierOptions Tiers { get; set; } = new();

    public ModelBackend BackendFor(string role)
        => Roles.TryGetValue(role, out var backend) ? backend : Backend;

    /// <summary>Sets the model of one backend (a picker's choice); empty restores the backend's default.</summary>
    public void SetModel(ModelBackend backend, string? model)
    {
        var value = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        switch (backend)
        {
            case ModelBackend.Claude:
                ClaudeModel = value;
                break;
            case ModelBackend.Copilot:
                CopilotModel = value;
                break;
            case ModelBackend.Anthropic when value is not null:
                Anthropic.Model = value;
                break;
            case ModelBackend.Foundry when value is not null:
                Foundry.Deployment = value;
                break;
            case ModelBackend.GitHubModels when value is not null:
                GitHubModels.Model = value;
                break;
            case ModelBackend.Local when value is not null:
                Local.Model = value;
                break;
        }
    }

    public string? ModelFor(ModelBackend backend) => backend switch
    {
        ModelBackend.Claude => ClaudeModel,
        ModelBackend.Copilot => CopilotModel,
        ModelBackend.Anthropic => Anthropic.Model,
        ModelBackend.Foundry => Foundry.Deployment,
        ModelBackend.GitHubModels => GitHubModels.Model,
        ModelBackend.Local => Local.Model,
        _ => null,
    };
}

/// <summary>
/// The light and heavy model of each backend, by backend name (claude, copilot, anthropic, foundry,
/// githubModels). Unset entries use <see cref="Default"/>: the fastest and the most capable model the
/// backend is known to offer. Foundry runs your own deployments, so it has no defaults.
/// </summary>
public sealed class ModelTierOptions
{
    public Dictionary<string, string> Light { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Heavy { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The model id for a tier on a backend; null means the backend's configured model.</summary>
    public string? ModelFor(ModelBackend backend, ModelTier tier)
    {
        var map = tier switch
        {
            ModelTier.Light => Light,
            ModelTier.Heavy => Heavy,
            _ => null,
        };
        if (map is null)
        {
            return null;
        }

        return map.TryGetValue(backend.ToString(), out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : Default(backend, tier);
    }

    public static string? Default(ModelBackend backend, ModelTier tier) => (backend, tier) switch
    {
        (ModelBackend.Claude, ModelTier.Light) => "haiku",
        (ModelBackend.Claude, ModelTier.Heavy) => "opus",
        (ModelBackend.Copilot, ModelTier.Light) => "gpt-5-mini",
        (ModelBackend.Copilot, ModelTier.Heavy) => "claude-opus-5",
        (ModelBackend.Anthropic, ModelTier.Light) => "claude-haiku-5-5",
        (ModelBackend.Anthropic, ModelTier.Heavy) => "claude-opus-5-5",
        (ModelBackend.GitHubModels, ModelTier.Light) => "openai/gpt-4o-mini",
        (ModelBackend.GitHubModels, ModelTier.Heavy) => "openai/gpt-4o",
        _ => null,
    };
}

public sealed class AnthropicOptions
{
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>Ceiling per response. Investigations carry whole reports, so this is generous.</summary>
    public int MaxOutputTokens { get; set; } = 16_000;

    /// <summary>Read from ANTHROPIC_API_KEY when not set here.</summary>
    public string? ApiKey { get; set; }
}

public sealed class FoundryOptions
{
    public string? Endpoint { get; set; }

    public string Deployment { get; set; } = "gpt-4o";
}

public sealed class GitHubModelsOptions
{
    public string Endpoint { get; set; } = "https://models.github.ai/inference";

    public string Model { get; set; } = "openai/gpt-4o-mini";

    /// <summary>Read from GITHUB_TOKEN when not set here (needs the models:read scope).</summary>
    public string? Token { get; set; }
}

/// <summary>
/// A model on this machine or network behind an OpenAI-compatible endpoint: Ollama
/// (<c>http://localhost:11434/v1</c>), Foundry Local, LM Studio (<c>http://localhost:1234/v1</c>) or vLLM.
/// Nothing leaves the machine, which suits tenants that may not send their data to a cloud model.
/// </summary>
public sealed class LocalModelOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434/v1";

    public string Model { get; set; } = "qwen3:8b";

    /// <summary>Most local servers ignore it; read from HUBWRIGHT_LOCAL_API_KEY when not set here.</summary>
    public string? ApiKey { get; set; }
}

/// <summary>Spending ceilings, checked before every model call from what the run logs have recorded.</summary>
public sealed class BudgetOptions
{
    /// <summary>Most a single run may spend, in USD. Null: no ceiling (written as null, so it is not read back as the default).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public decimal? MaxRunUsd { get; set; } = 5m;

    /// <summary>Most all runs together may spend per UTC day, in USD. Null: no ceiling (written as null, like <see cref="MaxRunUsd"/>).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public decimal? MaxDailyUsd { get; set; } = 25m;

    /// <summary>Per-model prices, USD per million tokens, overriding or extending the built-in table.</summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = [];
}

public sealed class ModelPrice
{
    public decimal InputPerMillion { get; set; }

    public decimal OutputPerMillion { get; set; }
}
