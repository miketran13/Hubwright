using Microsoft.Extensions.AI;
using Hubwright.Config;

namespace Hubwright.Chat;

/// <summary>
/// Drives the copilot CLI non-interactively with the prompt on stdin (<c>-s</c> keeps stdout to the
/// answer alone). Shell and write are denied and the built-in GitHub MCP server is off; when the
/// call carries an <see cref="McpBridge"/>, Hubwright's server is added and allowed.
/// </summary>
public sealed class CopilotCliChatClient : CliChatClientBase
{
    public const string ExecutableName = "copilot";

    private readonly string? _effort;

    public CopilotCliChatClient(string? model = null, string? effort = null)
        : base("copilot-cli", ExecutableName, model)
    {
        _effort = effort;
    }

    protected override ModelBackend Backend => ModelBackend.Copilot;

    internal override CliInvocation BuildInvocation(string? systemPrompt, string userPrompt, ChatOptions? options, McpBridge? tools)
        => Arguments(systemPrompt, userPrompt, options?.ModelId ?? Model, tools?.WriteCopilotConfig(CallDirectory()), _effort, tools?.ToolNames);

    internal static CliInvocation Arguments(string? systemPrompt, string userPrompt, string? model, string? mcpConfigPath, string? effort = null, IReadOnlyList<string>? toolNames = null)
    {
        var prompt = string.IsNullOrWhiteSpace(systemPrompt)
            ? userPrompt
            : $"{systemPrompt}\n\n---\n\n{userPrompt}";

        var args = new List<string>
        {
            "-s",
            "--log-level", "none",
            "--no-color",
            "--disable-builtin-mcps",
            "--no-custom-instructions",
            "--deny-tool", "shell",
            "--deny-tool", "write",
        };

        if (mcpConfigPath is not null)
        {
            args.AddRange(["--additional-mcp-config", "@" + mcpConfigPath, "--allow-tool", McpBridge.ServerName]);
        }

        args.Add("--available-tools");
        args.AddRange(mcpConfigPath is not null && toolNames is { Count: > 0 }
            ? toolNames.Select(name => $"{McpBridge.ServerName}-{name}")
            : ["__hubwright_no_tools__"]);

        if (!string.IsNullOrWhiteSpace(model))
        {
            args.AddRange(["--model", model]);
        }

        if (!string.IsNullOrWhiteSpace(effort))
        {
            args.AddRange(["--reasoning-effort", effort]);
        }

        return new CliInvocation(args, prompt);
    }

    internal override CliChatResult ParseResponse(string stdout)
        => new(stdout.Trim());
}
