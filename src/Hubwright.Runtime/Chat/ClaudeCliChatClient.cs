using System.Text.Json;
using Microsoft.Extensions.AI;
using Hubwright.Config;

namespace Hubwright.Chat;

/// <summary>
/// Drives <c>claude -p --restricted</c> with streamed JSON output. Every built-in tool is switched
/// off (<c>--tools ""</c>); when the call carries an <see cref="McpBridge"/>, the only tools are
/// Hubwright's, pre-allowed so print mode never stops to ask.
/// </summary>
public sealed class ClaudeCliChatClient : CliChatClientBase
{
    public const string ExecutableName = "claude";

    private readonly string? _effort;

    public ClaudeCliChatClient(string? model = null, string? effort = null)
        : base("claude-cli", ExecutableName, model)
    {
        _effort = effort;
    }

    protected override ModelBackend Backend => ModelBackend.Claude;

    internal override CliInvocation BuildInvocation(string? systemPrompt, string userPrompt, ChatOptions? options, McpBridge? tools)
        => Arguments(systemPrompt, userPrompt, options?.ModelId ?? Model, tools?.WriteClaudeConfig(CallDirectory()), _effort);

    internal static CliInvocation Arguments(string? systemPrompt, string userPrompt, string? model, string? mcpConfigPath, string? effort = null)
    {
        var args = new List<string> { "-p", "--restricted", "--tools", "" };
        args.AddRange(ClaudeEvents.Arguments);

        // Only the servers named here, never the user's own MCP servers.
        args.Add("--strict-mcp-config");
        if (mcpConfigPath is not null)
        {
            args.AddRange(["--mcp-config", mcpConfigPath, "--allowedTools", $"mcp__{McpBridge.ServerName}"]);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            args.AddRange(["--model", model]);
        }

        if (!string.IsNullOrWhiteSpace(effort))
        {
            args.AddRange(["--effort", effort]);
        }

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            args.AddRange(["--append-system-prompt", systemPrompt]);
        }

        // The prompt goes on stdin: data-heavy prompts exceed the Windows command-line limit.
        return new CliInvocation(args, userPrompt);
    }

    internal override CliChatResult ParseResponse(string stdout)
    {
        var json = ClaudeEvents.FindResult(stdout)
            ?? throw new InvalidOperationException($"claude CLI returned no JSON result. Output: {Truncate(stdout, 1000)}");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            var detail = root.TryGetProperty("result", out var errText) ? errText.GetString() : "unknown error";
            if (AgentLogin.ClassifyFailure(Backend, detail ?? "") is { } login)
            {
                throw new AgentAuthException(Backend, login);
            }

            throw new InvalidOperationException($"claude CLI reported an error: {detail}");
        }

        var text = root.TryGetProperty("result", out var result) ? result.GetString() ?? "" : "";
        var id = root.TryGetProperty("session_id", out var session) ? session.GetString() : null;

        UsageDetails? usage = null;
        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            var input = ReadInputTokens(usageElement);
            var output = ReadLong(usageElement, "output_tokens");
            usage = new UsageDetails
            {
                InputTokenCount = input,
                OutputTokenCount = output,
                TotalTokenCount = input + output,
            };

            // The CLI's own cost figure; on a subscription login it is the API-equivalent price.
            if (root.TryGetProperty("total_cost_usd", out var cost) && cost.TryGetDecimal(out var dollars))
            {
                CostMeteringChatClient.WriteCost(usage, dollars);
            }
        }

        return new CliChatResult(text, id, usage);
    }

    protected override string? DescribeLine(string line) => ClaudeEvents.Describe(line);

    protected override bool DescribesWords => true;

    /// <summary>Everything the model read: fresh input, input written to cache, and cache reads.</summary>
    internal static long ReadInputTokens(JsonElement usage)
        => ReadLong(usage, "input_tokens")
            + ReadLong(usage, "cache_creation_input_tokens")
            + ReadLong(usage, "cache_read_input_tokens");

    internal static long ReadLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    /// <summary>
    /// Pulls a JSON object out of stdout. The CLI can emit warnings before the JSON, so we scan for
    /// the first balanced top-level object rather than parsing the whole stream.
    /// </summary>
    internal static string? ExtractJsonObject(string stdout)
    {
        var start = stdout.IndexOf('{', StringComparison.Ordinal);
        while (start >= 0)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = start; i < stdout.Length; i++)
            {
                var ch = stdout[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return stdout[start..(i + 1)];
                    }
                }
            }

            start = stdout.IndexOf('{', start + 1);
        }

        return null;
    }
}
