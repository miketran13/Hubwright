using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Hubwright.Chat;

/// <summary>
/// Answers from a script instead of a model: deterministic playbook tests and offline demos
/// (<c>HUBWRIGHT_SCRIPT=&lt;file&gt;</c>). Each turn may first call tools, then answer with text,
/// so a scripted run exercises the same tool catalog, policy and executor as a real one.
/// </summary>
/// <remarks>The script is consumed in order, one turn per model call.</remarks>
public sealed class ScriptedChatClient(IReadOnlyList<ScriptedTurn> script) : IChatClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Queue<ScriptedTurn> _turns = new(script);

    public static ScriptedChatClient FromFile(string path)
        => new(JsonSerializer.Deserialize<List<ScriptedTurn>>(File.ReadAllText(path), Json) ?? []);

    public int Remaining => _turns.Count;

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!_turns.TryDequeue(out var turn))
        {
            throw new InvalidOperationException("The script has no turn left for this call.");
        }

        var tools = options?.Tools?.OfType<AIFunction>().ToDictionary(t => t.Name, StringComparer.Ordinal) ?? [];
        foreach (var call in turn.Calls)
        {
            if (!tools.TryGetValue(call.Tool, out var tool))
            {
                throw new InvalidOperationException($"The script calls '{call.Tool}', which this agent does not have.");
            }

            var arguments = new AIFunctionArguments(call.Arguments.ToDictionary(p => p.Key, p => (object?)p.Value));
            await tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
        }

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, turn.Text)) { ModelId = "script", Usage = new UsageDetails() };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType?.IsInstanceOfType(this) == true ? this : null;

    public void Dispose()
    {
    }
}

/// <summary>One scripted answer: tool calls (with JSON arguments) made first, then the text.</summary>
public sealed record ScriptedTurn
{
    public string Text { get; init; } = "";

    public IReadOnlyList<ScriptedCall> Calls { get; init; } = [];
}

public sealed record ScriptedCall(string Tool, Dictionary<string, JsonElement> Arguments);
