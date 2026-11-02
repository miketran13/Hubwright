using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Hubwright.Agents;
using Hubwright.Auth;
using Hubwright.Blueprints;
using Hubwright.Chat;
using Hubwright.Collectors;
using Hubwright.Config;
using Hubwright.Engine;
using Hubwright.Http;
using Hubwright.Journey;
using Hubwright.Mcp;
using Hubwright.Preview;
using Hubwright.Proposals;
using Hubwright.Reports;
using Hubwright.Rules;
using Hubwright.Skills;
using Hubwright.Snapshot;
using Hubwright.Storage;
using Hubwright.Theming;

namespace Hubwright.Cli;

/// <summary>AI: ask, chat and the MCP server.</summary>
internal static partial class Commands
{
    internal static async Task<int> AskAsync()
    {
        var question = string.Join(' ', cli.Positional.Skip(1));
        if (string.IsNullOrWhiteSpace(question))
        {
            throw new InvalidOperationException("Usage: hubwright ask \"Which sites are overshared?\"");
        }

        Console.WriteLine(await Session().Runner.RunAsync(cli.Option("role") ?? Roles.Assistant, question));
        return 0;
    }

    internal static async Task<int> ChatAsync()
    {
        var session = Session();
        var role = cli.Option("role") ?? Roles.Assistant;
        var history = new List<Microsoft.Extensions.AI.ChatMessage>();
        Console.WriteLine($"🏗️ {Roles.Title(role)} for {session.Store.Tenant} on {session.Config.Ai.BackendFor(role)}. Empty line to quit.");
        while (true)
        {
            Console.Write("\nyou › ");
            var line = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                return 0;
            }

            var answer = await session.Runner.RunAsync(role, line, history: history);
            history.Add(new(Microsoft.Extensions.AI.ChatRole.User, line));
            history.Add(new(Microsoft.Extensions.AI.ChatRole.Assistant, answer));
            Console.WriteLine($"\n{answer}");
        }
    }

    internal static async Task<int> McpAsync()
    {
        await HubwrightMcpServer.RunAsync(Session(), cli.Option("role") ?? Roles.Assistant);
        return 0;
    }
}
