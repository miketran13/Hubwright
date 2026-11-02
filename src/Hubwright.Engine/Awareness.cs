using System.Text;
using System.Text.Json;
using Hubwright.Auth;
using Hubwright.Http;
using Hubwright.Implementation;
using Hubwright.Runtime;

namespace Hubwright.Engine;

/// <summary>One part of the scope and permissions explanation: a heading and its points.</summary>
public sealed record AwarenessSection(string Title, IReadOnlyList<string> Points);

/// <summary>
/// What Hubwright reads, what it can change and how, for the person using it and the people they
/// tell: a short summary for Home and the full explanation behind it. The permission and operation
/// lists come from the code that enforces them (sign-in scopes, the write allow-list, the PnP cmdlet
/// list), so the explanation can't drift from what the app does. The person can record that they
/// have read it; a new version of the text asks again.
/// </summary>
public static class Awareness
{
    /// <summary>Raise when the explanation changes in a way people should read again.</summary>
    public const int Version = 2;

    public static IReadOnlyList<string> Summary { get; } =
    [
        "Scans, assessments, proposals and designs only read. Nothing changes in your tenant until a Build or Secure run.",
        "A run changes only the steps you pick, after a dry run and after you type the tenant name. Every write is logged first.",
        "Hubwright never deletes, archives or unregisters anything. Those stay steps you do yourself.",
        "Changes are visible to people in your organization: tell site owners and members before a run.",
    ];

    public static IReadOnlyList<AwarenessSection> Details()
    {
        static string Scope(string s) => s.Replace("https://graph.microsoft.com/", "Graph ", StringComparison.Ordinal);
        return
        [
            new("How you use Hubwright",
            [
                "Connect: register Hubwright's app (read-only) in your Entra ID and sign in as a SharePoint Administrator.",
                "Assess: a scan reads sites, hubs, navigation, sharing and permissions into a snapshot on this PC, and the skills grade it.",
                "Plan: the intranet plan, site designs, brand and look. All of this is files on this PC; your tenant is not touched.",
                "Build and Secure: you choose steps, preview exactly what each will send (the dry run), confirm with the tenant name, then Hubwright runs them.",
                "Run: read-only checks for drift, owner lists and scheduled scans.",
            ]),
            new("What it reads, and with which permissions",
            [
                "It signs in as you (delegated permissions) and sees only what you can see. Sites you're not a member of are listed but not opened.",
                "Read permissions: " + string.Join(", ", TenantAuth.GraphScopes.Select(Scope)) + ", and SharePoint AllSites.Read.",
                "The scan sends only GET requests, plus one read-only theme request. It can't write, even if the app holds write permissions.",
                "The snapshot, proposals and audit log stay in your Hubwright folder on this PC. Your tenant name and data are never put in the repository.",
            ]),
            new("What it can change, and only in a run you confirm",
            [
                "Write permissions are added to the app only when you reach Build (register-app.ps1 -Write), and an admin consents again: " + string.Join(", ", TenantAuth.ImplementGraphScopes.Select(Scope)) + ", SharePoint AllSites.FullControl.",
                "Documented SharePoint and Graph requests, nothing else: " + string.Join("; ", TenantWriter.Operations) + ".",
                "PnP PowerShell, for what SharePoint's REST API doesn't document, with these cmdlets only: " + string.Join(", ", PnpScripts.Cmdlets.Where(c => c != "Connect-PnPOnline")) + ".",
                "Tenant settings from the security baseline: each is read first, its old value is saved so you can restore it, and a stricter value is never loosened.",
                "Pages it creates (welcome posts, designed home pages) are drafts. A designed page is published and made a site's home page only when you choose Publish for that site and confirm it; the previous home page is recorded and kept.",
            ]),
            new("What it never does",
            [
                "It never deletes, archives or unregisters anything, and never removes navigation links. Those are guided steps you do in the admin center.",
                "It never adds you, or anyone, as a site admin or member to read a site.",
                "It never publishes a page you didn't ask it to publish, and never sends anything to your users.",
                "It never calls an undocumented endpoint. Steps without a documented API are guided: Hubwright opens the page and lists what to enter.",
                "It never sends tenant data anywhere except the AI backend you choose (Local keeps it on this PC). The AI only reads and proposes; you accept or reject.",
            ]),
            new("Who notices a change",
            [
                "New sites and hubs appear in search and in the SharePoint start page for the people who can open them.",
                "A theme, header or logo changes how a site looks for everyone who visits it.",
                "Hub association and navigation change where people find a site and what its menu shows.",
                "Sharing and baseline settings change what people can share, for the whole tenant.",
                "A new home page (published by the site owner, or by Hubwright when you choose Publish) changes what everyone who opens the site sees first.",
            ]),
            new("Before you run",
            [
                "Tell the site owners whose sites a step changes, and agree when. Publish a designed page only with the owner's agreement and after its members know.",
                "Tell your helpdesk what changes and when, so they can answer questions.",
                "Read each step's 'what this does' and its preview. Steps marked Changes or Caution change what exists.",
                "Remove the write permissions after the build (register-app.ps1 without -Write) and revoke the old consent.",
            ]),
            new("Afterwards, and how to undo",
            [
                "Audit log: every write is logged before it is sent and again with its result.",
                "Baseline: Secure → Baseline → Restore previous values puts back what a run replaced.",
                "Hub association, theme, header and home page can be changed back in SharePoint; the step's 'by hand' route says where. A published design's step shows the home page it replaced.",
                "Run → Drift shows anything that changed later.",
            ]),
        ];
    }

    /// <summary>The whole explanation as Markdown, for the CLI and for sharing with others.</summary>
    public static string Markdown()
    {
        var sb = new StringBuilder("# What Hubwright does in your tenant\n\n");
        foreach (var line in Summary)
        {
            sb.Append("- ").AppendLine(line);
        }

        foreach (var section in Details())
        {
            sb.Append("\n## ").AppendLine(section.Title).AppendLine();
            foreach (var point in section.Points)
            {
                sb.Append("- ").AppendLine(point);
            }
        }

        return sb.ToString();
    }

    private sealed record Record(int Version, string ReadBy, DateTimeOffset At);

    private static string RecordPath => HubwrightHome.PathFor("awareness.json");

    /// <summary>When this person last confirmed they read the current version; null when they haven't.</summary>
    public static DateTimeOffset? ReadAt()
    {
        try
        {
            return File.Exists(RecordPath) && JsonSerializer.Deserialize<Record>(File.ReadAllText(RecordPath)) is { } r && r.Version >= Version ? r.At : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    public static void Acknowledge(HubwrightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Directory.CreateDirectory(HubwrightHome.Directory);
        File.WriteAllText(RecordPath, JsonSerializer.Serialize(new Record(Version, Environment.UserName, DateTimeOffset.UtcNow)));
        session.Audit.Append("awareness.read", $"version {Version}");
    }
}
