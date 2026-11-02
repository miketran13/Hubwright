using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace Hubwright.Chat;

/// <summary>
/// How a CLI backend reaches Hubwright's tools. API backends get the tools in-process as
/// <see cref="AIFunction"/>s; the claude and copilot CLIs run their own tool loop, so they get the
/// same catalog from a local stdio MCP server instead (<c>hubwright mcp</c>), started by the CLI.
/// </summary>
/// <param name="Command">The executable that serves MCP on stdio.</param>
/// <param name="Arguments">Its arguments: the run the proposals belong to, the tools to expose.</param>
public sealed record McpBridge(string Command, IReadOnlyList<string> Arguments)
{
    public IReadOnlyList<string> ToolNames { get; init; } = [];

    /// <summary>The server name the CLIs see; tools appear as <c>mcp__hubwright__&lt;tool&gt;</c> in claude.</summary>
    public const string ServerName = "hubwright";

    private const string OptionKey = "hubwright.mcp";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Attaches the bridge to one call's options (the CLI clients are shared across calls).</summary>
    public ChatOptions Attach(ChatOptions? options)
    {
        var result = options?.Clone() ?? new ChatOptions();
        result.AdditionalProperties ??= [];
        result.AdditionalProperties[OptionKey] = this;
        return result;
    }

    public static McpBridge? From(ChatOptions? options)
        => options?.AdditionalProperties?.TryGetValue(OptionKey, out var value) == true ? value as McpBridge : null;

    /// <summary>Writes the claude <c>--mcp-config</c> file and returns its path.</summary>
    public string WriteClaudeConfig(string directory)
        => Write(directory, "mcp.claude.json", new JsonObject { ["type"] = "stdio" });

    /// <summary>Writes the copilot <c>--additional-mcp-config</c> file and returns its path.</summary>
    public string WriteCopilotConfig(string directory)
        => Write(directory, "mcp.copilot.json", new JsonObject { ["type"] = "local", ["tools"] = new JsonArray("*") });

    private string Write(string directory, string name, JsonObject server)
    {
        server["command"] = Command;
        server["args"] = new JsonArray([.. Arguments.Select(a => (JsonNode)JsonValue.Create(a)!)]);
        var root = new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = server } };

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, root.ToJsonString(Indented));
        return path;
    }
}
