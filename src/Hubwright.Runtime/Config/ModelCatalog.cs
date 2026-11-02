using System.Text.RegularExpressions;
using Hubwright.Runtime;

namespace Hubwright.Config;

/// <summary>One model a backend can run: the id passed to it, and how to show it.</summary>
public sealed record ModelChoice(ModelBackend Backend, string Id, string Label)
{
    public string? Note { get; init; }
}

/// <summary>
/// The models the pickers offer (Settings and the Ask chat), per backend. The Copilot CLI's list is
/// read from <c>copilot help config</c>, so it follows Copilot's own catalogue; the rest are known
/// names. Any other id still works: the pickers accept free text.
/// </summary>
public static partial class ModelCatalog
{
    /// <summary>Effort levels both CLIs accept (claude <c>--effort</c>, copilot <c>--reasoning-effort</c>).</summary>
    public static readonly IReadOnlyList<string> Efforts = ["low", "medium", "high", "xhigh", "max"];

    private static readonly string[] CopilotFallback =
    [
        "auto", "claude-sonnet-5", "claude-opus-5", "claude-fable-5.1", "claude-haiku-4.5", "gpt-5.5", "gpt-5.4", "gpt-5-mini", "gemini-3.8-flash",
    ];

    private static IReadOnlyList<string>? _copilot;

    [GeneratedRegex("^\\s*-\\s*\"(?<id>[A-Za-z0-9._:/-]+)\"\\s*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuotedItem();

    public static string BackendLabel(ModelBackend backend) => backend switch
    {
        ModelBackend.Claude => "Claude (subscription)",
        ModelBackend.Copilot => "GitHub Copilot (subscription)",
        ModelBackend.Anthropic => "Anthropic API",
        ModelBackend.Foundry => "Azure AI Foundry",
        ModelBackend.GitHubModels => "GitHub Models",
        ModelBackend.Local => "Local model (Ollama / Foundry Local)",
        _ => backend.ToString(),
    };

    /// <summary>Short name for a picker button: "Claude · sonnet".</summary>
    public static string ShortLabel(ModelBackend backend, string? model) => backend switch
    {
        ModelBackend.Claude => "Claude",
        ModelBackend.Copilot => "Copilot",
        ModelBackend.Anthropic => "Anthropic",
        ModelBackend.Foundry => "Foundry",
        ModelBackend.GitHubModels => "GitHub Models",
        ModelBackend.Local => "Local",
        _ => backend.ToString(),
    } + " · " + (string.IsNullOrWhiteSpace(model) ? "default" : model);

    /// <summary>Whether the backend takes an effort level.</summary>
    public static bool SupportsEffort(ModelBackend backend) => backend is ModelBackend.Claude or ModelBackend.Copilot;

    public static IReadOnlyList<ModelChoice> For(ModelBackend backend) => backend switch
    {
        ModelBackend.Claude =>
        [
            new(backend, "fable", "Fable (latest)") { Note = "Most capable" },
            new(backend, "opus", "Opus (latest)"),
            new(backend, "sonnet", "Sonnet (latest)") { Note = "Fast, strong" },
            new(backend, "haiku", "Haiku (latest)") { Note = "Fastest" },
            new(backend, "claude-fable-5-1", "claude-fable-5-1"),
            new(backend, "claude-opus-5-5", "claude-opus-5-5"),
            new(backend, "claude-sonnet-5-5", "claude-sonnet-5-5"),
            new(backend, "claude-haiku-5-5", "claude-haiku-5-5"),
        ],
        ModelBackend.Copilot => [.. CopilotModels().Select(id => new ModelChoice(backend, id, id) { Note = id == "auto" ? "Copilot picks" : null })],
        ModelBackend.Anthropic =>
        [
            new(backend, "claude-fable-5-1", "Claude Fable 5.1") { Note = "Most capable" },
            new(backend, "claude-opus-5-5", "Claude Opus 5.5"),
            new(backend, "claude-sonnet-5-5", "Claude Sonnet 5.5") { Note = "Fast, strong" },
            new(backend, "claude-haiku-5-5", "Claude Haiku 5.5") { Note = "Fastest" },
        ],
        ModelBackend.GitHubModels =>
        [
            new(backend, "openai/gpt-4o-mini", "openai/gpt-4o-mini"),
            new(backend, "openai/gpt-4o", "openai/gpt-4o"),
        ],
        ModelBackend.Local =>
        [
            new(backend, "qwen3:8b", "qwen3:8b") { Note = "Ollama, good tool use" },
            new(backend, "llama3.1:8b", "llama3.1:8b"),
            new(backend, "phi-4-mini", "phi-4-mini") { Note = "Foundry Local" },
        ],
        // Foundry runs your own deployment; its name is yours to type.
        _ => [],
    };

    /// <summary>The Copilot CLI's model list, read once per process from its help; a known list when it cannot be read.</summary>
    public static IReadOnlyList<string> CopilotModels()
    {
        if (_copilot is not null)
        {
            return _copilot;
        }

        IReadOnlyList<string> models = CopilotFallback;
        try
        {
            if (ProcessRunner.Exists("copilot"))
            {
                var result = ProcessRunner.RunAsync("copilot", ["help", "config"], Path.GetTempPath(), TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                var parsed = ParseCopilotHelp(result.StdOut);
                if (parsed.Count > 0)
                {
                    models = ["auto", .. parsed];
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or TimeoutException or OperationCanceledException)
        {
            // Not installed or not answering: the known list will do.
        }

        return _copilot = models;
    }

    /// <summary>The quoted ids listed under <c>`model`:</c> in <c>copilot help config</c>.</summary>
    public static IReadOnlyList<string> ParseCopilotHelp(string help)
    {
        var models = new List<string>();
        var inModel = false;
        foreach (var line in (help ?? "").Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            if (line.TrimStart().StartsWith("`model`", StringComparison.Ordinal))
            {
                inModel = true;
                continue;
            }

            if (!inModel)
            {
                continue;
            }

            if (QuotedItem().Match(line) is { Success: true } match)
            {
                models.Add(match.Groups["id"].Value);
            }
            else if (line.TrimStart().StartsWith('`') || (models.Count > 0 && line.Trim().Length == 0))
            {
                break;
            }
        }

        return models;
    }
}
