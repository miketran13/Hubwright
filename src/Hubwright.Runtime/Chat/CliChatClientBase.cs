using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Hubwright.Config;
using Hubwright.Runtime;

namespace Hubwright.Chat;

/// <summary>
/// Adapts a subscription-backed agent CLI into an <see cref="IChatClient"/>.
///
/// Neither a Claude Pro nor a GitHub Copilot subscription issues an API key, but their CLIs hold the
/// OAuth session, so we drive them in one-shot mode and present the result as a chat completion.
/// Agents are written once against IChatClient and the backend is configuration.
///
/// The CLIs never get their own tools (shell, file edits, web). When a call carries an
/// <see cref="McpBridge"/>, the CLI gets Hubwright's catalog over MCP and nothing else: the agent
/// can read market data and file proposals, and every change still goes through the policy.
/// </summary>
public abstract class CliChatClientBase : IChatClient
{
    private readonly ChatClientMetadata _metadata;

    protected CliChatClientBase(string providerName, string executable, string? model)
    {
        Executable = executable;
        Model = model;
        _metadata = new ChatClientMetadata(providerName, defaultModelId: model);
    }

    protected string Executable { get; }

    /// <summary>Which subscription this CLI bills, for login checks and error messages.</summary>
    protected abstract ModelBackend Backend { get; }

    protected string? Model { get; }

    /// <summary>Where the CLI process runs.</summary>
    public string WorkingDirectory { get; init; } = Path.GetTempPath();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var system = BuildSystemPrompt(list, options);
        var prompt = BuildUserPrompt(list);

        var invocation = BuildInvocation(system, prompt, options, McpBridge.From(options));
        var model = options?.ModelId ?? Model;

        // Every call is logged to the run and streamed live, so the admin can watch it work.
        var call = AgentLog.Begin(Backend.ToString().ToLowerInvariant(), model, prompt, system);
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(
                Executable,
                invocation.Arguments,
                WorkingDirectory,
                Timeout,
                invocation.StandardInput,
                onStdOutLine: line => call.Line(DescribeLine(line), DescribesWords),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            call.End(false, "", ex.Message);
            throw;
        }

        if (!result.Succeeded)
        {
            call.End(false, result.StdOut, Truncate(result.Combined, 2000), rawOutput: result.StdOut);
            if (AgentLogin.ClassifyFailure(Backend, result.Combined) is { } login)
            {
                throw new AgentAuthException(Backend, login);
            }

            throw new InvalidOperationException(
                $"{_metadata.ProviderName} CLI exited with code {result.ExitCode}: {Truncate(result.Combined, 2000)}");
        }

        CliChatResult parsed;
        try
        {
            parsed = ParseResponse(result.StdOut);
        }
        catch (Exception ex)
        {
            call.End(false, "", ex.Message, rawOutput: result.StdOut);
            throw;
        }

        call.End(true, parsed.Text, null, parsed.Usage?.InputTokenCount, parsed.Usage?.OutputTokenCount,
            parsed.Usage is { } u ? CostMeteringChatClient.ReadCost(u) : null, result.StdOut);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, parsed.Text))
        {
            ModelId = Model,
            ResponseId = parsed.ResponseId,
            CreatedAt = DateTimeOffset.UtcNow,
            FinishReason = ChatFinishReason.Stop,
            Usage = parsed.Usage,
        };
    }

    /// <summary>One-shot CLIs answer once, so the whole response is a single update.</summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ChatClientMetadata) ? _metadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    internal abstract CliInvocation BuildInvocation(string? systemPrompt, string userPrompt, ChatOptions? options, McpBridge? tools);

    internal abstract CliChatResult ParseResponse(string stdout);

    /// <summary>A readable form of one line of the CLI's output for the live view; null to skip it.</summary>
    protected virtual string? DescribeLine(string line) => line;

    /// <summary>
    /// Whether <see cref="DescribeLine"/> gives only the agent's own words and tool names (parsed
    /// events), never raw output that could hold a tool's result; only then do lines reach the run's step log.
    /// </summary>
    protected virtual bool DescribesWords => false;

    /// <summary>A folder for the MCP config files of the current run.</summary>
    protected static string CallDirectory()
        => Path.Combine(AgentLog.RunDirectory ?? Path.Combine(Path.GetTempPath(), "hubwright"), "mcp");

    /// <summary>Merges the instructions and any system messages into one system prompt.</summary>
    protected static string? BuildSystemPrompt(IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(options?.Instructions))
        {
            parts.Add(options!.Instructions!);
        }

        foreach (var message in messages.Where(m => m.Role == ChatRole.System))
        {
            var text = message.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    /// <summary>
    /// Flattens the non-system turns into a single prompt. Multi-turn history is rendered as a
    /// labelled transcript because these CLIs take exactly one prompt per invocation.
    /// </summary>
    protected static string BuildUserPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var turns = messages.Where(m => m.Role != ChatRole.System).ToList();

        if (turns.Count == 1)
        {
            return turns[0].Text;
        }

        var builder = new StringBuilder();
        foreach (var message in turns)
        {
            var label = message.Role == ChatRole.Assistant ? "Assistant" : "User";
            builder.Append("## ").AppendLine(label).AppendLine().AppendLine(message.Text).AppendLine();
        }

        builder.AppendLine("## Assistant").AppendLine().AppendLine("Respond as the assistant to the final user turn.");
        return builder.ToString();
    }

    protected static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + $"... [{value.Length - max} more chars]";
}

/// <summary>The command line of one CLI call; the prompt goes on stdin.</summary>
internal sealed record CliInvocation(IReadOnlyList<string> Arguments, string? StandardInput = null);

internal sealed record CliChatResult(string Text, string? ResponseId = null, UsageDetails? Usage = null);
