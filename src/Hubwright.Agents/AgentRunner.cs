using Microsoft.Extensions.AI;
using Hubwright.Chat;
using Hubwright.Config;
using Hubwright.Runtime;

namespace Hubwright.Agents;

/// <summary>How a CLI backend starts Hubwright's MCP server: this executable, <c>mcp --tenant &lt;t&gt; --role &lt;r&gt;</c>.</summary>
public sealed record McpCommand(string Executable, string Tenant);

/// <summary>
/// Runs one request on the role's backend. API backends (Anthropic, Foundry, GitHub Models, Local)
/// get the tool catalog in-process; the claude and copilot CLIs get the same tools from
/// <c>hubwright mcp</c>. Either way the reply is the agent's final text.
/// </summary>
public sealed class AgentRunner(ChatClientFactory models, Func<string, ToolCatalog> catalogFor, McpCommand? mcp)
{
    public int MaxToolIterations { get; init; } = 30;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    public async Task<string> RunAsync(string role, string prompt, bool tools = true, IList<ChatMessage>? history = null, CancellationToken cancellationToken = default)
    {
        var backend = models.Options.BackendFor(role);
        var options = new ChatOptions { Instructions = SensitiveText.Redact(SystemPrompt(role)) };
        var client = models.Create(backend);

        if (tools)
        {
            if (ChatClientFactory.IsCli(backend))
            {
                if (mcp is not null)
                {
                    var catalog = catalogFor(role).All();
                    options = new McpBridge(mcp.Executable, ["mcp", "--tenant", mcp.Tenant, "--role", role]) { ToolNames = [.. catalog.Select(t => t.Name)] }.Attach(options);
                }
            }
            else
            {
                options.Tools = [.. catalogFor(role).All()];
                client = new ChatClientBuilder(client).UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = MaxToolIterations).Build();
            }
        }

        var messages = new List<ChatMessage>(history ?? []) { new(ChatRole.User, SensitiveText.Redact(prompt)) };
        using (AgentLog.As(role, "ask"))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Timeout);
            try
            {
                var response = await client.GetResponseAsync(messages, options, deadline.Token).ConfigureAwait(false);
                return SensitiveText.Redact(response.Text);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"The {Roles.Title(role)} took longer than {Timeout.TotalMinutes:0} minutes.");
            }
        }
    }

    internal string SystemPrompt(string role)
    {
        var parts = new List<string> { Roles.Common, Roles.Prompt(role) };
        if (!string.IsNullOrWhiteSpace(models.Options.Instructions))
        {
            parts.Add("# The admin's standing instructions\n" + models.Options.Instructions);
        }

        parts.Add($"Today is {DateTimeOffset.Now:dddd d MMMM yyyy}.");
        return string.Join("\n\n", parts);
    }
}
