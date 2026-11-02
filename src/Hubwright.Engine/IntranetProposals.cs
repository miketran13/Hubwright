using Hubwright.Agents;
using Hubwright.Proposals;
using Hubwright.Skills;
using Hubwright.Storage;

namespace Hubwright.Engine;

/// <summary>
/// The intranet proposal workflow, the same for the CLI and the desktop app: seed the questionnaire
/// from the scan, ask the skills' questions, build the proposal from the rules, optionally let the
/// architect write the summary, and save it as HTML, Markdown and JSON next to the tenant's data.
/// </summary>
public static class IntranetProposals
{
    public static ProposalSeed Seed(HubwrightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = session.Workspace.Snapshot ?? throw new InvalidOperationException("Scan the tenant (or load a sample) first: a proposal starts from what the tenant has.");
        return IntranetProposalGenerator.Seed(snapshot);
    }

    /// <summary>Every skill question, with its default answer, for the Decisions step.</summary>
    public static IReadOnlyList<ProposalDecision> Questions(SkillLibrary skills, IReadOnlyDictionary<string, string>? answered = null)
    {
        ArgumentNullException.ThrowIfNull(skills);
        return [.. skills.Questions.Select(x => new ProposalDecision
        {
            Id = x.Question.Id,
            SkillId = x.Skill.Id,
            Question = x.Question.Text,
            Why = x.Question.Why,
            Answer = answered is not null && answered.TryGetValue(x.Question.Id, out var a) ? a : x.Question.Default,
        })];
    }

    public static IReadOnlyList<string> Choices(SkillLibrary skills, string questionId)
        => skills.Questions.FirstOrDefault(q => q.Question.Id == questionId).Question?.Choices ?? [];

    public static IntranetProposal Build(HubwrightSession session, ProposalAnswers answers, IReadOnlyList<string>? observations = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(answers);
        var current = session.Workspace.RequireAssessment();
        var decisions = Questions(session.Skills, answers.Decisions);
        var proposal = IntranetProposalGenerator.Generate(current.Snapshot, current, answers, session.Skills.Templates, decisions, observations, session.Skills.Patterns);
        var target = session.Workspace.Assess(proposal.Blueprint.ToSnapshot());
        proposal.ScoreAfter = target.Overall;
        proposal.Categories = [.. current.Categories.Select(c => c.Category).Union(target.Categories.Select(c => c.Category), StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .Select(c => new CategoryChange
            {
                Category = c,
                Before = current.Categories.FirstOrDefault(x => x.Category == c)?.Score,
                After = target.Categories.FirstOrDefault(x => x.Category == c)?.Score,
            })];
        return proposal;
    }

    /// <summary>The architect's summary, from the proposal's own numbers. Never changes the proposal's content.</summary>
    public static async Task<string> NarrateAsync(HubwrightSession session, IntranetProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proposal);
        var markdown = ProposalWriter.Markdown(proposal);
        var prompt = $"""
            Write the executive summary for this intranet proposal, for the organization's leadership:
            four short paragraphs (where the intranet is today, the target and why, the biggest changes
            for employees, and what the first month looks like), then three risks with how the plan
            handles them. Use only the numbers and names in the proposal below; cite rule ids in
            brackets where a point rests on one. Plain text, no headings.

            {markdown[..Math.Min(markdown.Length, 24000)]}
            """;
        return await session.Runner.RunAsync(Roles.Architect, prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves the proposal (JSON, HTML and Markdown) and logs it; returns the folder.</summary>
    public static string Save(HubwrightSession session, IntranetProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proposal);
        var folder = session.Store.SaveIntranetProposal(proposal, Html(session, proposal), ProposalWriter.Markdown(proposal));
        session.Audit.Append("proposal.new", $"{proposal.Name}: {proposal.Blueprint.Hubs.Count} hubs, {proposal.NewSites.Count} new sites → {folder}");
        return folder;
    }

    /// <summary>The proposal page with its hints and the structure preview against the current scan.</summary>
    public static string Html(HubwrightSession session, IntranetProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ProposalWriter.Html(proposal, session.Skills.Templates, session.Skills.HintBook(), session.Workspace.Snapshot);
    }

    /// <summary>Re-renders a saved proposal's HTML with the current hints, preview and scan (the content doesn't change).</summary>
    public static string Render(HubwrightSession session, string folder)
    {
        ArgumentNullException.ThrowIfNull(session);
        var path = Path.Combine(folder, "proposal.html");
        File.WriteAllText(path, Html(session, TenantStore.LoadIntranetProposal(folder)));
        return path;
    }
    /// <summary>Writes an edited proposal back to its folder (theme, logo): JSON, HTML and Markdown.</summary>
    public static void Update(HubwrightSession session, string folder, IntranetProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proposal);
        TenantStore.WriteIntranetProposal(folder, proposal, Html(session, proposal), ProposalWriter.Markdown(proposal));
        session.Audit.Append("proposal.edit", $"{proposal.Name} → {folder}");
    }

    /// <summary>Makes the proposal's target the tenant's blueprint, so Blueprint, Preview and Export work on it.</summary>
    public static string UseAsBlueprint(HubwrightSession session, IntranetProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proposal);
        var path = session.Store.SaveBlueprint(proposal.Blueprint);
        session.Audit.Append("proposal.use", $"{proposal.Name} → {path}");
        return path;
    }
}
