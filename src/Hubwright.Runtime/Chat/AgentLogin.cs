using System.Text.Json;
using System.Text.Json.Nodes;
using Hubwright.Config;
using Hubwright.Runtime;

namespace Hubwright.Chat;

/// <summary>
/// The agent CLI is installed but not signed in, or its session has expired. Carries the one
/// command that fixes it, so the CLI and the dashboard can say exactly that instead of a stack trace.
/// </summary>
public sealed class AgentAuthException(ModelBackend backend, string message) : InvalidOperationException(message)
{
    public ModelBackend Backend { get; } = backend;
}

/// <summary>Whether a developer's agent CLI is installed and signed in.</summary>
public sealed record AgentLoginStatus
{
    public required ModelBackend Backend { get; init; }
    public bool Installed { get; init; }
    public bool LoggedIn { get; init; }
    public string? Account { get; init; }
    /// <summary>subscription | apiKey | token | unknown.</summary>
    public string? Method { get; init; }
    public string Detail { get; init; } = "";
    public string? Fix { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Ready => Installed && LoggedIn;
}

/// <summary>
/// Checks that the Claude and Copilot CLIs are logged in — not just on PATH — before a run spends
/// ten minutes getting to the first model call. Developers log in themselves, in advance; this only
/// tells them when they have not, or when the login has expired.
/// </summary>
public static class AgentLogin
{
    public const string ClaudeFix = "run `claude auth login` (or start `claude` and type /login), then try again";
    public const string CopilotFix = "run `copilot login`, then try again";

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly string[] CopilotTokenVariables = ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];

    /// <summary>Phrases the CLIs print when the login is missing or stale.</summary>
    private static readonly string[] AuthFailures =
    [
        "not logged in", "please run /login", "run /login", "please log in", "login required",
        "invalid api key", "authentication_error", "authentication failed", "oauth token has expired",
        "token has expired", "token expired", "session expired", "401 unauthorized", "copilot login",
        "no authentication", "not authenticated", "re-authenticate", "reauthenticate",
    ];

    /// <summary>The friendly message for a failed call, or null when the failure is not about the login.</summary>
    public static string? ClassifyFailure(ModelBackend backend, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var text = output.ToLowerInvariant();
        if (!AuthFailures.Any(p => text.Contains(p, StringComparison.Ordinal)))
        {
            return null;
        }

        return backend switch
        {
            ModelBackend.Copilot => $"GitHub Copilot CLI is not logged in or its session has expired — {CopilotFix}.",
            _ => $"Claude CLI is not logged in or its session has expired — {ClaudeFix}.",
        };
    }

    /// <summary>
    /// The login status of one backend. Cached for ten minutes per user, so the dashboard header and
    /// a run's pre-flight do not each spawn the CLI; <paramref name="refresh"/> forces a fresh check.
    /// </summary>
    public static async Task<AgentLoginStatus> CheckAsync(ModelBackend backend, bool refresh = false, CancellationToken cancellationToken = default)
    {
        if (!refresh && ReadCache(backend) is { } cached)
        {
            return cached;
        }

        var status = backend switch
        {
            ModelBackend.Claude => await CheckClaudeAsync(cancellationToken).ConfigureAwait(false),
            ModelBackend.Copilot => await CheckCopilotAsync(cancellationToken).ConfigureAwait(false),
            _ => new AgentLoginStatus { Backend = backend, Installed = true, LoggedIn = true, Method = "apiKey", Detail = "not a CLI login" },
        };

        WriteCache(status);
        return status;
    }

    /// <summary>Throws <see cref="AgentAuthException"/> unless every listed CLI backend is signed in.</summary>
    public static async Task EnsureAsync(IEnumerable<ModelBackend> backends, CancellationToken cancellationToken = default)
    {
        foreach (var backend in backends.Where(b => b is ModelBackend.Claude or ModelBackend.Copilot).Distinct())
        {
            var status = await CheckAsync(backend, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!status.Installed)
            {
                throw new AgentAuthException(backend, $"{status.Detail} — {status.Fix}");
            }

            if (!status.LoggedIn)
            {
                // A cached "logged out" may be stale: the developer may have just logged in.
                status = await CheckAsync(backend, refresh: true, cancellationToken).ConfigureAwait(false);
                if (!status.LoggedIn)
                {
                    throw new AgentAuthException(backend, $"{status.Detail} — {status.Fix}");
                }
            }
        }
    }

    public static void ForgetCache()
    {
        var path = CachePath;
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async Task<AgentLoginStatus> CheckClaudeAsync(CancellationToken cancellationToken)
    {
        if (!ProcessRunner.Exists(ClaudeCliChatClient.ExecutableName))
        {
            return new AgentLoginStatus
            {
                Backend = ModelBackend.Claude,
                Detail = "claude CLI not found on PATH",
                Fix = "npm install -g @anthropic-ai/claude-code, then `claude auth login`",
            };
        }

        if (HubwrightEnvironment.ClaudeApiAuthVariables.FirstOrDefault(v => Environment.GetEnvironmentVariable(v) is { Length: > 0 }) is { } variable)
        {
            return new AgentLoginStatus
            {
                Backend = ModelBackend.Claude,
                Installed = true,
                LoggedIn = true,
                Method = "apiKey",
                Detail = $"claude CLI on an API account ({variable})",
            };
        }

        var result = await Run(ClaudeCliChatClient.ExecutableName, ["auth", "status", "--json"], null, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);

        if (result is not null && ClaudeCliChatClient.ExtractJsonObject(result.StdOut) is { } json)
        {
            return ParseClaudeStatus(json);
        }

        // An older CLI without `auth status`: ask it something tiny instead.
        var probe = await Run(ClaudeCliChatClient.ExecutableName, ["-p", "--output-format", "json"], "Reply with exactly: OK", TimeSpan.FromSeconds(90), cancellationToken)
            .ConfigureAwait(false);
        return FromProbe(ModelBackend.Claude, probe, ClaudeFix);
    }

    internal static AgentLoginStatus ParseClaudeStatus(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? [];
        var loggedIn = node["loggedIn"]?.GetValue<bool>() == true;
        var account = node["email"]?.GetValue<string>();
        var method = node["authMethod"]?.GetValue<string>();
        var org = node["orgName"]?.GetValue<string>();

        return new AgentLoginStatus
        {
            Backend = ModelBackend.Claude,
            Installed = true,
            LoggedIn = loggedIn,
            Account = account,
            Method = method is null ? "subscription" : method.Contains("api", StringComparison.OrdinalIgnoreCase) ? "apiKey" : "subscription",
            Detail = loggedIn
                ? $"claude CLI signed in{(account is null ? "" : $" as {account}")}{(org is null ? "" : $" ({org})")}"
                : "claude CLI is not logged in",
            Fix = loggedIn ? null : ClaudeFix,
        };
    }

    private static async Task<AgentLoginStatus> CheckCopilotAsync(CancellationToken cancellationToken)
    {
        if (!ProcessRunner.Exists(CopilotCliChatClient.ExecutableName))
        {
            return new AgentLoginStatus
            {
                Backend = ModelBackend.Copilot,
                Detail = "copilot CLI not found on PATH",
                Fix = "winget install GitHub.Copilot (or npm install -g @github/copilot), then `copilot login`",
            };
        }

        if (CopilotTokenVariables.FirstOrDefault(v => Environment.GetEnvironmentVariable(v) is { Length: > 0 }) is { } variable)
        {
            return new AgentLoginStatus
            {
                Backend = ModelBackend.Copilot,
                Installed = true,
                LoggedIn = true,
                Method = "token",
                Detail = $"copilot CLI with a token from {variable}",
            };
        }

        if (CopilotConfigAccount() is { } account)
        {
            return new AgentLoginStatus
            {
                Backend = ModelBackend.Copilot,
                Installed = true,
                LoggedIn = true,
                Account = account,
                Method = "subscription",
                Detail = $"copilot CLI signed in as {account}",
            };
        }

        // No recorded login: confirm with a tiny prompt rather than guess.
        var probe = await Run(
            CopilotCliChatClient.ExecutableName,
            ["-s", "--no-color", "--log-level", "none", "--deny-tool", "shell", "--deny-tool", "write"],
            "Reply with exactly: OK",
            TimeSpan.FromSeconds(90),
            cancellationToken).ConfigureAwait(false);
        return FromProbe(ModelBackend.Copilot, probe, CopilotFix);
    }

    /// <summary>The account recorded in ~/.copilot/config.json by `copilot login`, if any.</summary>
    internal static string? CopilotConfigAccount(string? configPath = null)
    {
        var path = configPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "config.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            var users = node?["loggedInUsers"]?.AsArray();
            if (users is null || users.Count == 0)
            {
                return null;
            }

            return node?["lastLoggedInUser"]?["login"]?.GetValue<string>()
                ?? users[0]?["login"]?.GetValue<string>()
                ?? "a GitHub account";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static AgentLoginStatus FromProbe(ModelBackend backend, ProcessResult? probe, string fix)
    {
        var name = backend == ModelBackend.Copilot ? "copilot" : "claude";
        if (probe is null)
        {
            return new AgentLoginStatus { Backend = backend, Installed = true, Detail = $"{name} CLI did not answer", Fix = fix };
        }

        var output = probe.Combined;
        if (probe.Succeeded && ClassifyFailure(backend, output) is null)
        {
            return new AgentLoginStatus { Backend = backend, Installed = true, LoggedIn = true, Method = "subscription", Detail = $"{name} CLI answered a test prompt" };
        }

        return new AgentLoginStatus
        {
            Backend = backend,
            Installed = true,
            Detail = ClassifyFailure(backend, output) is not null
                ? $"{name} CLI is not logged in or its session has expired"
                : $"{name} CLI failed a test prompt (exit {probe.ExitCode})",
            Fix = fix,
        };
    }

    private static async Task<ProcessResult?> Run(string exe, string[] args, string? stdin, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await ProcessRunner.RunAsync(exe, args, Path.GetTempPath(), timeout, stdin, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ cache

    private static string CachePath => HubwrightHome.PathFor("agent-login.json");

    private static AgentLoginStatus? ReadCache(ModelBackend backend)
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            var all = JsonSerializer.Deserialize<Dictionary<string, AgentLoginStatus>>(File.ReadAllText(CachePath), CacheJson);
            return all is not null
                && all.TryGetValue(backend.ToString(), out var status)
                && DateTimeOffset.UtcNow - status.CheckedAt < CacheFor
                    ? status
                    : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static void WriteCache(AgentLoginStatus status)
    {
        try
        {
            var all = File.Exists(CachePath)
                ? JsonSerializer.Deserialize<Dictionary<string, AgentLoginStatus>>(File.ReadAllText(CachePath), CacheJson) ?? []
                : [];
            all[status.Backend.ToString()] = status;
            File.WriteAllText(CachePath, JsonSerializer.Serialize(all, CacheJson));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A cache that cannot be written only costs a re-check.
        }
    }

    private static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}
