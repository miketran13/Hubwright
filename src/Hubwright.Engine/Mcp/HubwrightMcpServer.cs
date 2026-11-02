using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Hubwright.Agents;
using Hubwright.Engine;

namespace Hubwright.Mcp;

/// <summary>
/// <c>hubwright mcp</c>: Hubwright's read-only tools over stdio. The claude and copilot CLIs start it
/// for an agent, and any MCP client can use it too (Claude Desktop, VS Code, Copilot): point the
/// client at <c>hubwright mcp --tenant &lt;name&gt;</c>. Stdout belongs to the protocol, so logs go to stderr.
/// </summary>
public static class HubwrightMcpServer
{
    public static async Task RunAsync(HubwrightSession session, string role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Roles.All.Contains(role, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Unknown role '{role}'. Roles: {string.Join(", ", Roles.All)}.");
        }

        var tools = new ToolCatalog(session.Workspace, session.Proposals, role).All().Select(f => McpServerTool.Create(f)).ToList();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "hubwright", Version = typeof(HubwrightMcpServer).Assembly.GetName().Version?.ToString() ?? "1.0" })
            .WithStdioServerTransport()
            .WithTools(tools);

        using var host = builder.Build();
        await host.RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
