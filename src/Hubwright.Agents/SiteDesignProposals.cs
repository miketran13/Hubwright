using System.Text.Json;
using Hubwright.Designs;
using Hubwright.Proposals;
using Hubwright.Snapshot;
using Hubwright.Storage;

namespace Hubwright.Agents;

/// <summary>
/// A redesign of one site, filed for a person to accept. It is checked first (known sections, real
/// templates, plain labels); accepting it changes the intranet proposal's design, never the tenant.
/// </summary>
public static class SiteDesignProposals
{
    public const string DesignFile = "design.json";
    public const string CurrentFile = "current.json";
    public const string FolderFile = "proposal";

    /// <summary>The tenant's latest intranet proposal and its folder, or null when there is none.</summary>
    public static (string Folder, IntranetProposal Proposal)? Latest(TenantStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.IntranetProposals().FirstOrDefault() is { } folder ? (folder, TenantStore.LoadIntranetProposal(folder)) : null;
    }

    /// <summary>Files the redesign; returns the errors instead when it can't be kept.</summary>
    public static (Proposal? Filed, IReadOnlyList<string> Errors) File(Workspace workspace, ProposalStore proposals, string folder, SiteDesign current, SiteDesign design, string summary, string by)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(design);
        var errors = SiteDesigner.Validate(design, workspace.Skills.Templates);
        if (errors.Count > 0)
        {
            return (null, errors);
        }

        design.Url = current.Url;
        design.IsNew = current.IsNew;
        design.Role = current.Role;
        design.Kind = current.Kind;
        design.Blocked = current.Blocked;
        design.Origin = DesignOrigin.Agent;
        design.ChangeNote = summary;
        var filed = proposals.Add(new Proposal
        {
            Kind = ProposalKind.SiteDesign,
            Title = $"Site design: {current.Title} → {design.PatternName}",
            Summary = summary,
            Target = workspace.Store.Tenant,
            Files = new()
            {
                [DesignFile] = JsonSerializer.Serialize(design, TenantSnapshot.Json),
                [CurrentFile] = JsonSerializer.Serialize(current, TenantSnapshot.Json),
                [FolderFile] = Path.GetFileName(folder),
            },
            ProposedBy = by,
        });
        return (filed, []);
    }

    /// <summary>The proposed design in a filed proposal.</summary>
    public static SiteDesign Read(Proposal proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return JsonSerializer.Deserialize<SiteDesign>(proposal.Files[DesignFile], TenantSnapshot.Json) ?? throw new InvalidDataException("The proposal has no design.");
    }

    /// <summary>The intranet proposal folder a design proposal is for.</summary>
    public static string FolderOf(TenantStore store, Proposal proposal)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(proposal);
        var folder = Path.Combine(store.IntranetProposalsDirectory, Path.GetFileName(proposal.Files.GetValueOrDefault(FolderFile) ?? ""));
        return Directory.Exists(folder) ? folder : throw new InvalidOperationException($"The intranet proposal this design is for ({proposal.Files.GetValueOrDefault(FolderFile)}) is no longer there.");
    }

    /// <summary>A design as text, for agents and the CLI.</summary>
    public static string Describe(SiteDesign d) => string.Join("\n",
        new[]
        {
            $"{d.Title} ({d.Url}) · {d.Role}, {(d.IsNew ? "new" : "existing")} {(d.Kind == SiteKind.Communication ? "communication" : "team")} site",
            $"Pattern: {d.PatternName} ({d.Pattern}){(d.Confident ? "" : " · not confident: confirm the purpose")} · chosen by {d.Origin.ToString().ToLowerInvariant()}",
            $"Purpose: {d.Purpose}",
            $"Template: {d.TemplateName ?? "none"} · header {d.HeaderLayout} · in build: {(d.Apply ? "yes" : "no")}{(d.Blocked is null ? "" : " · blocked: " + d.Blocked)}",
            $"Sections: {string.Join(", ", d.Sections)}",
            $"Navigation: {(d.Navigation.Count == 0 ? "none" : string.Join(" · ", d.Navigation))}",
            $"Audience: {d.Audience}",
        }
        .Concat(d.Reasons.Select(r => "Why: " + r))
        .Concat(d.Practices.Select(p => "Practice: " + p))
        .Concat(d.ChangeNote is null ? [] : ["Change: " + d.ChangeNote]));
}
