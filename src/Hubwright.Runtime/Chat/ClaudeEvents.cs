using System.Text.Json;

namespace Hubwright.Chat;

/// <summary>
/// Reads <c>claude -p --output-format stream-json --verbose</c>: one JSON event per line as the
/// agent works, ending with a <c>result</c> event that carries the same fields as the single-object
/// JSON output (result, is_error, usage, total_cost_usd, num_turns, session_id).
/// </summary>
public static class ClaudeEvents
{
    /// <summary>The arguments that switch claude's print mode to streamed events.</summary>
    public static readonly string[] Arguments = ["--output-format", "stream-json", "--verbose"];

    /// <summary>
    /// A readable line for an event, for the live view: the agent's text, the tools it calls
    /// ("→ Edit src/Orders.cs"), tool errors. Null for events not worth showing (and the result).
    /// </summary>
    public static string? Describe(string line)
    {
        if (!line.TrimStart().StartsWith('{'))
        {
            return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = Str(root, "type");
            switch (type)
            {
                case "system" when Str(root, "subtype") == "init":
                    return Str(root, "model") is { } model ? $"session started · {model}" : "session started";

                case "assistant" when Content(root) is { } content:
                {
                    var parts = new List<string>();
                    foreach (var block in content.EnumerateArray())
                    {
                        switch (Str(block, "type"))
                        {
                            case "text" when Str(block, "text") is { Length: > 0 } text:
                                parts.Add(text.Trim());
                                break;
                            case "tool_use":
                                parts.Add($"→ {Str(block, "name")}{Target(block)}");
                                break;
                        }
                    }

                    return parts.Count == 0 ? null : string.Join("\n", parts);
                }

                case "user" when Content(root) is { } content:
                {
                    var errors = content.EnumerateArray()
                        .Where(b => Str(b, "type") == "tool_result" && b.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
                        .Select(b => b.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "tool error")
                        .Select(text => $"  ✗ {Short(text ?? "", 200)}")
                        .ToList();
                    return errors.Count == 0 ? null : string.Join("\n", errors);
                }

                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return line.Trim();
        }
    }

    /// <summary>The final <c>result</c> event's JSON; else whatever single JSON object the output holds.</summary>
    public static string? FindResult(string stdout)
    {
        foreach (var line in stdout.Split('\n').Reverse())
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith('{') || !trimmed.Contains("\"result\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(trimmed);
                if (Str(document.RootElement, "type") == "result")
                {
                    return trimmed;
                }
            }
            catch (JsonException)
            {
            }
        }

        return ClaudeCliChatClient.ExtractJsonObject(stdout);
    }

    private static JsonElement? Content(JsonElement root)
        => root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? content
                : null;

    /// <summary>What a tool call acts on: the file, the command, the pattern.</summary>
    private static string Target(JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        foreach (var name in new[] { "file_path", "path", "command", "pattern", "url", "description" })
        {
            if (Str(input, name) is { Length: > 0 } value)
            {
                return " " + Short(value, 140);
            }
        }

        return "";
    }

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Short(string value, int max)
    {
        var line = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }
}
