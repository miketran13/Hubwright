using System.ClientModel;
using Anthropic;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI;
using Hubwright.Config;
using Hubwright.Runtime;

namespace Hubwright.Chat;

/// <summary>Whether a backend is ready to answer, for <c>hubwright doctor</c> and the Settings page.</summary>
public sealed record ProviderHealth(bool Ready, string Detail)
{
    public static ProviderHealth Ok(string detail) => new(true, detail);

    public static ProviderHealth Missing(string detail) => new(false, detail);
}

/// <summary>
/// Resolves a <see cref="ModelBackend"/> into an <see cref="IChatClient"/>. Every backend produces
/// the same interface, so roles are written once and Claude Pro / Copilot / an Anthropic key /
/// Foundry / GitHub Models / a local model is configuration. Every client is wrapped in
/// <see cref="CostMeteringChatClient"/> so spend is recorded the same way whichever answers.
/// </summary>
/// <param name="override">Answers every backend instead (tests, replays).</param>
public sealed class ChatClientFactory(AiOptions options, IChatClient? @override = null)
{
    private readonly Dictionary<ModelBackend, IChatClient> _cache = [];
    private readonly Dictionary<(ModelBackend, string), IChatClient> _modelCache = [];
    private readonly ModelPricing _pricing = new(options.Budget.Prices);

    public AiOptions Options => options;

    public IChatClient Create(ModelBackend backend)
    {
        if (_cache.TryGetValue(backend, out var cached))
        {
            return cached;
        }

        var client = @override ?? backend switch
        {
            ModelBackend.Claude => new ClaudeCliChatClient(options.ClaudeModel, options.Effort),
            ModelBackend.Copilot => new CopilotCliChatClient(options.CopilotModel, options.Effort),
            ModelBackend.Anthropic => CreateAnthropic(),
            ModelBackend.Foundry => CreateFoundry(),
            ModelBackend.GitHubModels => CreateGitHubModels(),
            ModelBackend.Local => CreateLocal(),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown model backend."),
        };

        client = new CostMeteringChatClient(client, _pricing, @override is null && !IsCli(backend) ? options.Budget : null);
        _cache[backend] = client;
        return client;
    }

    /// <summary>
    /// A client for a specific model on a backend (a tier's light or heavy model). Null or the
    /// backend's own model returns the backend's usual client.
    /// </summary>
    public IChatClient Create(ModelBackend backend, string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || string.Equals(model, options.ModelFor(backend), StringComparison.OrdinalIgnoreCase))
        {
            return Create(backend);
        }

        var key = (backend, model.Trim());
        if (_modelCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var client = @override ?? backend switch
        {
            ModelBackend.Claude => new ClaudeCliChatClient(key.Item2, options.Effort),
            ModelBackend.Copilot => new CopilotCliChatClient(key.Item2, options.Effort),
            ModelBackend.Anthropic => CreateAnthropic(key.Item2),
            ModelBackend.Foundry => CreateFoundry(key.Item2),
            ModelBackend.GitHubModels => CreateGitHubModels(key.Item2),
            ModelBackend.Local => CreateLocal(key.Item2),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "Unknown model backend."),
        };

        client = new CostMeteringChatClient(client, _pricing, @override is null && !IsCli(backend) ? options.Budget : null);
        _modelCache[key] = client;
        return client;
    }

    /// <summary>True for the subscription CLIs, which get tools over MCP rather than in-process.</summary>
    public static bool IsCli(ModelBackend backend) => backend is ModelBackend.Claude or ModelBackend.Copilot;

    private IChatClient CreateAnthropic(string? model = null)
    {
        var key = AnthropicKey()
            ?? throw new InvalidOperationException(
                "Anthropic backend selected but no API key found. Set ANTHROPIC_API_KEY, or ai.anthropic.apiKey in hubwright.json.");

        var client = new AnthropicClient { ApiKey = key };
        return client.AsIChatClient(model ?? options.Anthropic.Model, options.Anthropic.MaxOutputTokens);
    }

    private string? AnthropicKey()
        => options.Anthropic.ApiKey is { Length: > 0 } configured
            ? configured
            : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } fromEnvironment
                ? fromEnvironment
                : null;

    private IChatClient CreateFoundry(string? deployment = null)
    {
        var endpoint = options.Foundry.Endpoint
            ?? Environment.GetEnvironmentVariable("AZURE_AI_FOUNDRY_ENDPOINT")
            ?? throw new InvalidOperationException(
                "Foundry backend selected but no endpoint configured. Set ai.foundry.endpoint or AZURE_AI_FOUNDRY_ENDPOINT.");

        var azure = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential());
        return azure.GetChatClient(deployment ?? options.Foundry.Deployment).AsIChatClient();
    }

    private string? GitHubToken()
        => options.GitHubModels.Token
            ?? Environment.GetEnvironmentVariable("HUBWRIGHT_GITHUB_TOKEN")
            ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

    private IChatClient CreateGitHubModels(string? model = null)
    {
        var token = GitHubToken()
            ?? throw new InvalidOperationException(
                "GitHub Models backend selected but no token found. Set ai.githubModels.token or GITHUB_TOKEN (models:read).");

        // GitHub Models is OpenAI-wire-compatible, so the OpenAI SDK works with a redirected endpoint.
        var openAi = new OpenAIClient(new ApiKeyCredential(token), new OpenAIClientOptions { Endpoint = new Uri(options.GitHubModels.Endpoint) });
        return openAi.GetChatClient(model ?? options.GitHubModels.Model).AsIChatClient();
    }

    private IChatClient CreateLocal(string? model = null)
    {
        // Local servers speak the OpenAI wire format; most accept any key, so a placeholder is fine.
        var key = options.Local.ApiKey ?? Environment.GetEnvironmentVariable("HUBWRIGHT_LOCAL_API_KEY") ?? "local";
        var openAi = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(options.Local.Endpoint) });
        return openAi.GetChatClient(model ?? options.Local.Model).AsIChatClient();
    }

    private static bool ClaudeOnApiAccount()
        => HubwrightEnvironment.ClaudeApiAuthVariables.Any(v => Environment.GetEnvironmentVariable(v) is { Length: > 0 });

    /// <summary>Readiness probe; never throws and never calls a model.</summary>
    public ProviderHealth CheckHealth(ModelBackend backend) => backend switch
    {
        ModelBackend.Claude => ProcessRunner.Exists(ClaudeCliChatClient.ExecutableName)
            ? ProviderHealth.Ok(ClaudeOnApiAccount() ? "claude CLI on PATH (API account)" : "claude CLI on PATH (subscription)")
            : ProviderHealth.Missing("claude CLI not found: npm install -g @anthropic-ai/claude-code"),

        ModelBackend.Copilot => ProcessRunner.Exists(CopilotCliChatClient.ExecutableName)
            ? ProviderHealth.Ok("copilot CLI on PATH (Copilot subscription)")
            : ProviderHealth.Missing("copilot CLI not found: winget install GitHub.Copilot"),

        ModelBackend.Anthropic => AnthropicKey() is not null
            ? ProviderHealth.Ok($"{options.Anthropic.Model} via the Anthropic API (API key)")
            : ProviderHealth.Missing("no ANTHROPIC_API_KEY"),

        ModelBackend.Foundry => (options.Foundry.Endpoint ?? Environment.GetEnvironmentVariable("AZURE_AI_FOUNDRY_ENDPOINT")) is { } endpoint
            ? ProviderHealth.Ok($"endpoint {endpoint}, deployment {options.Foundry.Deployment}")
            : ProviderHealth.Missing("no ai.foundry.endpoint / AZURE_AI_FOUNDRY_ENDPOINT"),

        ModelBackend.GitHubModels => GitHubToken() is not null
            ? ProviderHealth.Ok($"{options.GitHubModels.Model} via {options.GitHubModels.Endpoint}")
            : ProviderHealth.Missing("no GITHUB_TOKEN"),

        ModelBackend.Local => Uri.TryCreate(options.Local.Endpoint, UriKind.Absolute, out _)
            ? ProviderHealth.Ok($"{options.Local.Model} via {options.Local.Endpoint} (local, start Ollama or Foundry Local first)")
            : ProviderHealth.Missing("ai.local.endpoint is not a valid URL"),

        _ => ProviderHealth.Missing("unknown backend"),
    };
}
