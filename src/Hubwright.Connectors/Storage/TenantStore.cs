using System.Globalization;
using Hubwright.Blueprints;
using Hubwright.Runtime;
using Hubwright.Snapshot;

namespace Hubwright.Storage;

/// <summary>
/// One tenant's folder under <c>~/.hubwright/tenants/&lt;name&gt;/</c>: snapshots (one JSON per scan),
/// blueprints, reports and <c>overrides.yaml</c>. Tenant data stays on this machine.
/// </summary>
public sealed class TenantStore
{
    public TenantStore(string tenant, string? home = null)
    {
        Tenant = Clean(tenant);
        Directory = Path.Combine(home ?? HubwrightHome.Directory, "tenants", Tenant);
    }

    public string Tenant { get; }

    public string Directory { get; }

    public string SnapshotsDirectory => Ensure("snapshots");

    public string BlueprintsDirectory => Ensure("blueprints");

    public string ReportsDirectory => Ensure("reports");

    public string OverridesPath => Path.Combine(Directory, "overrides.yaml");

    public static IReadOnlyList<string> Tenants(string? home = null)
    {
        var root = Path.Combine(home ?? HubwrightHome.Directory, "tenants");
        return System.IO.Directory.Exists(root) ? [.. System.IO.Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)] : [];
    }

    public string SaveSnapshot(TenantSnapshot snapshot)
    {
        var path = Path.Combine(SnapshotsDirectory, snapshot.CapturedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
        Write(path, snapshot.ToJson());
        return path;
    }

    /// <summary>Snapshot files, newest first.</summary>
    public IReadOnlyList<string> Snapshots()
        => [.. System.IO.Directory.GetFiles(SnapshotsDirectory, "*.json").OrderDescending(StringComparer.Ordinal)];

    public TenantSnapshot? Latest() => Snapshots().FirstOrDefault() is { } path ? TenantSnapshot.FromJson(File.ReadAllText(path)) : null;

    public string SaveBlueprint(Blueprint blueprint, string? name = null)
    {
        var path = Path.Combine(BlueprintsDirectory, Clean(name ?? blueprint.Name) + ".json");
        Write(path, blueprint.ToJson());
        return path;
    }

    public IReadOnlyList<string> Blueprints() => [.. System.IO.Directory.GetFiles(BlueprintsDirectory, "*.json").OrderByDescending(File.GetLastWriteTimeUtc)];

    public Blueprint? LatestBlueprint() => Blueprints().FirstOrDefault() is { } path ? Blueprint.FromJson(File.ReadAllText(path)) : null;

    public string IntranetProposalsDirectory => Ensure("proposals");

    /// <summary>Saves a proposal as its own folder: proposal.json, proposal.html and proposal.md.</summary>
    public string SaveIntranetProposal(Proposals.IntranetProposal proposal, string html, string markdown)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var folder = Path.Combine(IntranetProposalsDirectory, $"{proposal.Created.UtcDateTime:yyyyMMdd-HHmmss}-{Clean(proposal.Answers.Intranet.Organization)}");
        WriteIntranetProposal(folder, proposal, html, markdown);
        return folder;
    }

    public static void WriteIntranetProposal(string folder, Proposals.IntranetProposal proposal, string html, string markdown)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        Write(Path.Combine(folder, "proposal.json"), proposal.ToJson());
        Write(Path.Combine(folder, "proposal.html"), html);
        Write(Path.Combine(folder, "proposal.md"), markdown);
    }

    /// <summary>Proposal folders, newest first.</summary>
    public IReadOnlyList<string> IntranetProposals()
        => [.. System.IO.Directory.GetDirectories(IntranetProposalsDirectory).Where(d => File.Exists(Path.Combine(d, "proposal.json"))).OrderDescending(StringComparer.Ordinal)];

    public static Proposals.IntranetProposal LoadIntranetProposal(string folder)
        => Proposals.IntranetProposal.FromJson(File.ReadAllText(Path.Combine(folder, "proposal.json")));

    public string SaveReport(string name, string content)
    {
        var path = Path.Combine(ReportsDirectory, name);
        Write(path, content);
        return path;
    }

    private string Ensure(string child) => System.IO.Directory.CreateDirectory(Path.Combine(Directory, child)).FullName;

    internal static void Write(string path, string content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, overwrite: true);
    }

    public static string Clean(string name)
    {
        var cleaned = new string([.. name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]).Trim('-');
        return cleaned.Length == 0 ? "default" : cleaned;
    }
}
