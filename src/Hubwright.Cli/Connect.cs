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

/// <summary>Stage 1, Connect: setup, samples, sign-in, scan, import and the journey status.</summary>
internal static partial class Commands
{
    internal static int Sample()
    {
        var name = (cli.At(1) ?? "fabrikam").ToLowerInvariant();
        var snapshot = SampleTenants.ByName(name);
        var store = new TenantStore(tenant ?? name);
        var path = store.SaveSnapshot(snapshot);
        Console.WriteLine($"Sample tenant '{snapshot.Tenant}' saved: {path}");
        Console.WriteLine($"Next: hubwright assess --tenant {store.Tenant}   ·   hubwright preview --tenant {store.Tenant} --from snapshot --open");
        return 0;
    }

    internal static int Connect()
    {
        var config = HubwrightConfig.Load();
        if (tenant is not null)
        {
            config.Tenant.Name = TenantStore.Clean(tenant);
        }

        config.Tenant.ClientId = cli.Option("client-id") ?? config.Tenant.ClientId ?? throw new InvalidOperationException("--client-id is required; create one with tools/register-app.ps1.");
        config.Tenant.TenantId = cli.Option("tenant-id") ?? config.Tenant.TenantId;
        config.Tenant.SignIn = Method() ?? config.Tenant.SignIn;
        config.Save();
        Console.WriteLine($"Saved app {config.Tenant.ClientId}{(config.Tenant.Name is null ? "" : $" for {config.Tenant.RootUrl}")}, sign-in with {config.Tenant.SignIn}. Next: hubwright login");
        return 0;
    }

    internal static SignInMethod? Method() => cli.Option("method")?.ToLowerInvariant() switch
    {
        null => null,
        "browser" => SignInMethod.Browser,
        "windows" or "wam" => SignInMethod.Windows,
        "device" or "devicecode" or "device-code" => SignInMethod.DeviceCode,
        var other => throw new InvalidOperationException($"Unknown --method '{other}': use browser, windows or device."),
    };

    internal static async Task<SignInOutcome> SignInAsync()
    {
        var config = HubwrightConfig.Load();
        config.Tenant.SignIn = Method() ?? config.Tenant.SignIn;
        if (config.Tenant.SignIn == SignInMethod.Browser)
        {
            Console.Error.WriteLine("  opening your browser to sign in…");
        }

        var outcome = await TenantSignIn.RunAsync(config, null, null, CancellationToken.None);
        Console.WriteLine($"Signed in as {outcome.Account.Username} · https://{outcome.Tenant}.sharepoint.com");
        if (outcome.Corrected)
        {
            Console.WriteLine($"  Note: Graph says this tenant is '{outcome.Tenant}', not '{outcome.Typed}'. The connection now uses '{outcome.Tenant}'.");
        }

        return outcome;
    }

    internal static async Task<int> LoginAsync()
    {
        await SignInAsync();
        return 0;
    }

    internal static async Task<int> LogoutAsync()
    {
        await Session().Auth().SignOutAsync();
        Console.WriteLine("Signed out.");
        return 0;
    }

    internal static async Task<int> ScanAsync()
    {
        var session = Session();
        if (session.Config.Tenant.ClientId is null || session.Config.Tenant.Name is null || await session.Auth().CurrentAsync() is null)
        {
            if (cli.Flag("no-prompt"))
            {
                session.Audit.Append("scan.skipped", $"{session.Store.Tenant} · nobody signed in");
                throw new SignInRequiredException("Nobody is signed in, and --no-prompt says not to ask. Run `hubwright login` once.");
            }

            tenant = (await SignInAsync()).Tenant;
            session = Session();
        }

        var auth = session.Auth();
        var progress = new Progress<ScanProgress>(p => Console.Error.Write($"\r  {p.Step}{(p.Total > 0 ? $" {p.Done}/{p.Total}" : "")}            "));
        var snapshot = await session.Collector(auth).CollectAsync(cli.Option("scope"), progress, CancellationToken.None);
        Console.Error.WriteLine();
        var path = session.Store.SaveSnapshot(snapshot);
        session.Audit.Append("scan", $"{snapshot.Sites.Count} sites, {snapshot.ScanIssues.Count} issues → {path}");
        Console.WriteLine($"{snapshot.Sites.Count} sites, {snapshot.Hubs.Count} hubs, {snapshot.ScanIssues.Count} things not read. Saved {path}");
        UnreadNote(snapshot);
        foreach (var group in snapshot.ScanIssues.GroupBy(i => i.Area))
        {
            Console.WriteLine($"  · {group.Key}: {group.Count()} ({group.First().Reason})");
        }

        return 0;
    }

    internal static int Import()
    {
        var file = cli.At(1) ?? throw new InvalidOperationException("Usage: hubwright import <snapshot.json> [--tenant name]");
        var snapshot = TenantSnapshot.FromJson(File.ReadAllText(file));
        snapshot.Source = SnapshotSource.Import;
        var store = new TenantStore(tenant ?? (string.IsNullOrWhiteSpace(snapshot.Tenant) ? "imported" : snapshot.Tenant));
        var path = store.SaveSnapshot(snapshot);
        new AuditLog().Append("import", $"{file} → {path}");
        Console.WriteLine($"Imported {snapshot.Sites.Count} sites into tenant '{store.Tenant}': {path}");
        return 0;
    }

    internal static async Task<int> SetupAsync()
    {
        var config = HubwrightConfig.Load();
        Console.WriteLine("Setup: what Hubwright needs to connect and build.");
        Console.WriteLine(string.IsNullOrWhiteSpace(config.Tenant.ClientId)
            ? "✗ App: not registered. Run tools/register-app.ps1 (read-only by default), or Setup in the desktop app."
            : $"✓ App: {config.Tenant.ClientId}");
        Console.WriteLine(string.IsNullOrWhiteSpace(config.Tenant.Name)
            ? "✗ Tenant: not signed in yet. Run `hubwright login`."
            : $"✓ Tenant: {config.Tenant.RootUrl}");
        var ready = await PnpAsync();
        if (!string.IsNullOrWhiteSpace(config.Tenant.Name))
        {
            Console.WriteLine("\nNext: `hubwright scan`, then `hubwright status`.");
        }

        return ready && !string.IsNullOrWhiteSpace(config.Tenant.ClientId) && !string.IsNullOrWhiteSpace(config.Tenant.Name) ? 0 : 1;
    }

    internal static async Task<bool> PnpAsync()
    {
        {
            var status = await Hubwright.Tools.PnpShell.CheckAsync();
            Console.WriteLine((status.Ready ? "✓ " : "✗ ") + status.Detail);
            if (!status.Ready && cli.Flag("install"))
            {
                if (status.PowerShell is null)
                {
                    Console.WriteLine($"Installing PowerShell 7: {Hubwright.Tools.PnpShell.InstallPowerShellCommand}");
                    using var winget = Process.Start(new ProcessStartInfo("winget") { ArgumentList = { "install", "--id", "Microsoft.PowerShell", "--source", "winget" }, UseShellExecute = false });
                    await winget!.WaitForExitAsync();
                    status = await Hubwright.Tools.PnpShell.CheckAsync();
                }

                if (status.PowerShell is not null && status.PnpVersion is null)
                {
                    Console.WriteLine($"Installing PnP PowerShell: {Hubwright.Tools.PnpShell.InstallPnpCommand}");
                    using var install = Process.Start(new ProcessStartInfo(status.PowerShell) { ArgumentList = { "-NoProfile", "-Command", Hubwright.Tools.PnpShell.InstallPnpCommand }, UseShellExecute = false });
                    await install!.WaitForExitAsync();
                }

                status = await Hubwright.Tools.PnpShell.CheckAsync();
                Console.WriteLine((status.Ready ? "✓ " : "✗ ") + status.Detail);
            }
            else if (!status.Ready)
            {
                Console.WriteLine("PnP PowerShell is only needed to build (home site, navigation, templates, header, logo). Run `hubwright setup --install`, or install it yourself.");
            }

            return status.Ready;
        }
    }

    internal static int Status()
    {
        var session = Session();
        var stages = JourneyStatus.For(session);
        Console.WriteLine($"Intranet journey for {session.Store.Tenant}:");
        foreach (var stage in stages)
        {
            var mark = stage.State switch { StageState.Done => "✓", StageState.InProgress => "◐", _ => "○" };
            Console.WriteLine($"  {mark} {stage.Title,-8} {stage.Status}");
        }

        if (IntranetJourney.Next(stages) is { } next)
        {
            Console.WriteLine($"\nNext: {next.NextAction}\n  {next.Command}");
        }
        else
        {
            Console.WriteLine("\nEvery stage is done. Scan regularly to keep it that way.");
        }

        return 0;
    }
}
