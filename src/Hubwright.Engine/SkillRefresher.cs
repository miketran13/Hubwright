using System.Globalization;
using System.Text;
using Hubwright.Agents;
using Hubwright.Skills;
using Hubwright.Storage;

namespace Hubwright.Engine;

/// <summary>
/// <c>hubwright skills refresh</c>: re-reads every documented rule's source and checks its quote is
/// still there. For each skill with a missing quote it files a proposal. With AI on, the skills
/// librarian drafts the edit and the proposal carries it. Accepting a proposal writes a new
/// version of the skill into the user's skills folder. Built-in skills are never edited in place.
/// </summary>
public sealed class SkillRefresher(HubwrightSession session, ISourceFetcher fetcher)
{
    public async Task<(IReadOnlyList<DriftResult> Results, IReadOnlyList<Proposal> Proposals)> RefreshAsync(
        IReadOnlyList<Skill> skills, bool useAi, Action<string>? report, CancellationToken cancellationToken)
    {
        var results = await SourceDrift.CheckAsync(skills, fetcher, cancellationToken).ConfigureAwait(false);
        var proposals = new List<Proposal>();
        foreach (var group in results.Where(r => r.Status == DriftStatus.QuoteMissing).GroupBy(r => r.SkillId))
        {
            var skill = skills.First(s => s.Id == group.Key);
            var summary = new StringBuilder();
            summary.AppendLine(CultureInfo.InvariantCulture, $"{group.Count()} quote(s) in {skill.Id} {skill.Version} no longer appear on their source page:");
            foreach (var drift in group)
            {
                summary.AppendLine(CultureInfo.InvariantCulture, $"- {drift.RuleId}: {drift.Detail} ({drift.Url})");
            }

            var files = new Dictionary<string, string>();
            if (useAi)
            {
                report?.Invoke($"  asking the librarian about {skill.Id}…");
                var page = await fetcher.FetchAsync(new Uri(group.First().Url), cancellationToken).ConfigureAwait(false);
                var prompt = $"""
                    Skill {skill.Id} {skill.Version}. These rules' quotes no longer appear on the source page:
                    {summary}

                    Current skill files:
                    {SkillFiles(skill)}

                    Current page text (first 30,000 characters, markup removed):
                    {Truncate(SourceDrift.Normalize(page.Text ?? ""), 30_000)}
                    """;
                var answer = await session.Runner.RunAsync(Roles.Librarian, prompt, tools: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                files["librarian.md"] = answer;
            }

            proposals.Add(session.Proposals.Add(new Proposal
            {
                Kind = ProposalKind.SkillUpdate,
                Title = $"Review skill {skill.Id}: {group.Count()} source quote(s) changed",
                Summary = summary.ToString(),
                Target = skill.Id,
                Files = files,
                ProposedBy = useAi ? Roles.Librarian : "skills refresh",
            }));
        }

        session.Audit.Append("skills.refresh", $"{results.Count} quotes checked (documented rules and templates), {results.Count(r => r.Status == DriftStatus.QuoteMissing)} quotes missing, {proposals.Count} proposals");
        return (results, proposals);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>rules.yaml and templates.yaml, whichever the skill has, as fenced blocks for the librarian.</summary>
    private static string SkillFiles(Skill skill)
    {
        var sb = new StringBuilder();
        foreach (var name in new[] { SkillLoader.RulesFile, SkillLoader.TemplatesFile })
        {
            var path = Path.Combine(skill.Directory, name);
            if (File.Exists(path))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{name}:\n```yaml\n{Truncate(File.ReadAllText(path), 20_000)}\n```");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Copies a skill into the user's folder with the version bumped and every source's reviewed date
    /// set to today. Used when a person has checked the sources and accepts the skill as current, or
    /// after they edit the copy by hand.
    /// </summary>
    public static string Fork(Skill skill, DateOnly today, string? targetRoot = null)
    {
        var target = Path.Combine(targetRoot ?? HubwrightSession.UserSkillsDirectory, skill.Id);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(skill.Directory))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        var version = skill.ParsedVersion;
        var next = new Version(version.Major, version.Minor, Math.Max(0, version.Build) + 1).ToString(3);
        var manifest = Path.Combine(target, SkillLoader.SkillFile);
        var stamp = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var lines = File.ReadAllLines(manifest).Select(line =>
            line.StartsWith("version:", StringComparison.Ordinal) ? $"version: {next}"
            : line.TrimStart().StartsWith("reviewed:", StringComparison.Ordinal) ? line[..line.IndexOf("reviewed:", StringComparison.Ordinal)] + $"reviewed: {stamp}"
            : line);
        File.WriteAllLines(manifest, lines);
        return target;
    }
}
