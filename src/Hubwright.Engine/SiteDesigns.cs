using Hubwright.Agents;
using Hubwright.Designs;
using Hubwright.Proposals;
using Hubwright.Storage;

namespace Hubwright.Engine;

/// <summary>
/// Site designs, the same for the CLI and the desktop app: list and preview them, choose another
/// pattern, include or leave out a site from the build, ask the architect for a redesign, and accept
/// a redesign an agent filed. Every change is saved to the intranet proposal, logged, and the build
/// plan is rebuilt (finished steps stay finished). Nothing here touches the tenant.
/// </summary>
public static class SiteDesigns
{
    /// <summary>The latest intranet proposal and its folder; throws when there is none.</summary>
    public static (string Folder, IntranetProposal Proposal) Latest(HubwrightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return SiteDesignProposals.Latest(session.Store) ?? throw new InvalidOperationException("No intranet proposal yet: make one first (Intranet plan, or `hubwright proposal new`).");
    }

    public static SiteDesign Require(IntranetProposal proposal, string site)
        => SiteDesigner.Find(proposal, site) ?? throw new InvalidOperationException($"No site {site} in the proposal. `hubwright design` lists them.");

    /// <summary>The clickable site preview, opened at one site, with an optional design in place of the proposal's.</summary>
    public static string Preview(HubwrightSession session, IntranetProposal proposal, string? site = null, SiteDesign? instead = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proposal);
        if (instead is not null)
        {
            proposal = IntranetProposal.FromJson(proposal.ToJson());
            SiteDesigner.Replace(proposal, instead);
        }

        return SitePrototype.Render(proposal, session.Skills.Templates, proposal.Blueprint.Logo, start: site ?? instead?.Url);
    }

    public static SiteDesign UsePattern(HubwrightSession session, string folder, IntranetProposal proposal, string site, string patternId)
    {
        ArgumentNullException.ThrowIfNull(session);
        var current = Require(proposal, site);
        var pattern = session.Skills.Patterns.FirstOrDefault(p => string.Equals(p.Id, patternId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No pattern {patternId}. Known: {string.Join(", ", session.Skills.Patterns.Select(p => p.Id))}.");
        if (!Templates.TemplateMatcher.SameFamily(pattern.Kind, current.Kind))
        {
            throw new InvalidOperationException($"{pattern.Name} is a pattern for {(pattern.Kind == Snapshot.SiteKind.Communication ? "communication" : "team")} sites, and {current.Title} is not one.");
        }

        var design = SiteDesigner.WithPattern(current, pattern, session.Skills.Templates);
        Save(session, folder, proposal, design, $"{current.Title}: pattern {current.Pattern} → {pattern.Id}");
        return design;
    }

    public static SiteDesign Include(HubwrightSession session, string folder, IntranetProposal proposal, string site, bool include)
    {
        ArgumentNullException.ThrowIfNull(session);
        var design = Require(proposal, site).Copy();
        if (include && design.Blocked is not null)
        {
            throw new InvalidOperationException($"{design.Title} can't be built yet: {design.Blocked}");
        }

        design.Apply = include;
        Save(session, folder, proposal, design, $"{design.Title}: {(include ? "included in" : "left out of")} the build");
        return design;
    }

    /// <summary>The drafted page's address on the site (it exists once the draft step ran).</summary>
    public static string DraftUrl(SiteDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return $"{design.Url.TrimEnd('/')}/SitePages/{Implementation.PnpScripts.DesignPageName}.aspx";
    }

    /// <summary>
    /// Gets one site's design ready to apply to the real site: included in the build, with Publish on
    /// or off, the build plan updated (finished steps stay finished). Returns the plan and the steps to
    /// run: the draft, and with <paramref name="publish"/> the automatic publish step. Nothing is sent
    /// here; the caller confirms with the person and runs them with <see cref="Implementations.Runner"/>.
    /// </summary>
    public static (Implementation.ImplementationPlan Plan, IReadOnlyList<string> Steps) Prepare(HubwrightSession session, string folder, IntranetProposal proposal, string site, bool publish)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Workspace.Snapshot?.Source is Snapshot.SnapshotSource.Sample or Snapshot.SnapshotSource.Blueprint)
        {
            throw new InvalidOperationException("This tenant is sample or design-only data: there is no real site to apply it to. Scan your tenant and make a plan from that scan.");
        }

        var design = Require(proposal, site).Copy();
        if (design.Blocked is not null)
        {
            throw new InvalidOperationException($"{design.Title} can't be built yet: {design.Blocked}");
        }

        if (!design.Apply || design.Publish != publish)
        {
            design.Apply = true;
            design.Publish = publish;
            Save(session, folder, proposal, design, $"{design.Title}: {(publish ? "draft, publish and make home page" : "draft only")}");
        }

        var plan = Implementations.For(session, folder);
        var steps = plan.Steps.Where(s => s.Kind == Implementation.StepKind.Automatic && string.Equals(s.Target?.TrimEnd('/'), design.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && (s.Operation == Implementation.StepOperation.AddDesignPage || (publish && s.Operation == Implementation.StepOperation.PublishDesignPage))).Select(s => s.Id).ToList();
        return steps.Count > 0 ? (plan, steps) : throw new InvalidOperationException($"The build plan has no design step for {design.Title}.");
    }

    /// <summary>Puts back Include in build and Publish as they were before <see cref="Prepare"/>, when the person cancels.</summary>
    public static void Restore(HubwrightSession session, string folder, IntranetProposal proposal, string site, bool apply, bool publish)
    {
        ArgumentNullException.ThrowIfNull(session);
        var design = Require(proposal, site).Copy();
        if (design.Apply == apply && design.Publish == publish)
        {
            return;
        }

        design.Apply = apply;
        design.Publish = publish;
        Save(session, folder, proposal, design, $"{design.Title}: cancelled, back to {(apply ? publish ? "draft and publish" : "draft only" : "left out of the build")}");
    }

    /// <summary>Where the site's design stands in the build plan: drafted, published, or not yet; null without a plan.</summary>
    public static string? Progress(string folder, SiteDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        var path = Path.Combine(folder, Implementations.FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var plan = Implementation.ImplementationPlan.FromJson(File.ReadAllText(path));
        bool For(Implementation.ImplementationStep s) => string.Equals(s.Target?.TrimEnd('/'), design.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        var draft = plan.Steps.FirstOrDefault(s => s.Operation == Implementation.StepOperation.AddDesignPage && For(s));
        var publish = plan.Steps.FirstOrDefault(s => s.Id.StartsWith("design-publish-", StringComparison.Ordinal) && For(s));
        if (publish?.IsFinished == true)
        {
            return $"Published and the home page{(publish.Outputs.GetValueOrDefault("previous") is { Length: > 0 } before ? $" (before: {before})" : "")}. {publish.Result}";
        }

        return draft is null ? "Not in the build plan yet."
            : draft.IsFinished ? $"Draft on the site: {draft.Result}"
            : draft.Status == Implementation.StepStatus.Failed ? $"The draft failed: {draft.Result}"
            : "Not applied yet.";
    }

    /// <summary>Asks the architect to redesign a site; it files a suggestion with propose_site_design. Returns its answer.</summary>
    public static async Task<string> AskAsync(HubwrightSession session, string site, string request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var (_, proposal) = Latest(session);
        var design = Require(proposal, site);
        var prompt = $"""
            Redesign the site "{design.Title}" ({design.Url}) in the latest intranet proposal.
            The admin asks: {request.Trim()}

            Its current design:
            {SiteDesignProposals.Describe(design)}

            Read the patterns (list_site_patterns) if you need them, then call propose_site_design once
            for this site with your change and a one-paragraph reason. Then tell the admin in three
            short lines what you proposed and that it waits in AI suggestions for them to accept.
            """;
        session.Audit.Append("design.ask", $"{design.Title}: {request.Trim()}");
        return await session.Runner.RunAsync(Roles.Architect, prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Accepts a redesign an agent filed: checked again, then saved to the proposal it was for.</summary>
    public static SiteDesign Accept(HubwrightSession session, Proposal suggestion)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(suggestion);
        var folder = SiteDesignProposals.FolderOf(session.Store, suggestion);
        var proposal = TenantStore.LoadIntranetProposal(folder);
        var design = SiteDesignProposals.Read(suggestion);
        var errors = SiteDesigner.Validate(design, session.Skills.Templates);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("The design no longer passes its checks: " + string.Join(" ", errors));
        }

        design.Origin = DesignOrigin.Agent;
        design.Apply = design.Blocked is null;
        Save(session, folder, proposal, design, $"{design.Title}: accepted suggestion {suggestion.Id}");
        return design;
    }

    private static void Save(HubwrightSession session, string folder, IntranetProposal proposal, SiteDesign design, string what)
    {
        SiteDesigner.Replace(proposal, design);
        IntranetProposals.Update(session, folder, proposal);
        session.Audit.Append("design.edit", what);
        if (File.Exists(Path.Combine(folder, Implementations.FileName)))
        {
            Implementations.Rebuild(session, folder);
        }
    }
}
