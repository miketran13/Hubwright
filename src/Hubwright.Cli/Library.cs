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

/// <summary>Skills and the AI suggestions inbox.</summary>
internal static partial class Commands
{
    internal static async Task<int> SkillsAsync()
    {
        var session = Session();
        switch (cli.At(1)?.ToLowerInvariant())
        {
            case "validate":
            {
                var issues = SkillValidator.Validate(session.Skills, DateOnly.FromDateTime(DateTime.UtcNow));
                foreach (var issue in issues)
                {
                    Console.WriteLine(issue);
                }

                var errors = issues.Count(i => i.Level == IssueLevel.Error);
                Console.WriteLine($"{session.Skills.Skills.Count} skills, {session.Skills.Rules.Count} rules ({session.Skills.Rules.Count(r => r.Basis == Basis.Documented)} documented, {session.Skills.Rules.Count(r => r.Basis == Basis.Heuristic)} heuristic): {errors} error(s), {issues.Count - errors} warning(s).");
                return errors == 0 ? 0 : 1;
            }

            case "new":
            {
                var id = cli.At(2) ?? throw new InvalidOperationException("Usage: hubwright skills new <id> [--title \"...\"] [--dir folder]");
                var dir = SkillScaffold.Create(cli.Option("dir") ?? HubwrightSession.UserSkillsDirectory, id, cli.Option("title") ?? id, DateOnly.FromDateTime(DateTime.UtcNow));
                Console.WriteLine($"Created {dir}. Edit SKILL.md and rules.yaml, then run `hubwright skills validate`.");
                return 0;
            }

            case "fork":
            {
                var skill = session.Skills.Find(cli.At(2) ?? "") ?? throw new InvalidOperationException("Usage: hubwright skills fork <id>");
                var dir = SkillRefresher.Fork(skill, DateOnly.FromDateTime(DateTime.UtcNow));
                Console.WriteLine($"Copied {skill.Id} to {dir} as your own layer. Edit it there; it now replaces the {skill.Layer} version.");
                return 0;
            }

            case "refresh":
            {
                var skills = cli.At(2) is { } id ? [session.Skills.Find(id) ?? throw new InvalidOperationException($"No skill {id}.")] : session.Skills.Skills;
                Console.Error.WriteLine($"  checking {skills.Sum(s => s.Rules.Count(r => r.Basis == Basis.Documented))} documented rules and {skills.Sum(s => s.Templates.Count)} templates against their sources…");
                var (results, proposals) = await new SkillRefresher(session, new HttpSourceFetcher()).RefreshAsync(skills, cli.Flag("ai"), m => Console.Error.WriteLine(m), CancellationToken.None);
                foreach (var r in results.Where(r => r.Status != DriftStatus.QuoteFound))
                {
                    Console.WriteLine($"{r.Status,-13} {r.RuleId,-34} {r.Detail}");
                }

                Console.WriteLine($"{results.Count(r => r.Status == DriftStatus.QuoteFound)} of {results.Count} quotes still match. {proposals.Count} proposal(s) filed{(proposals.Count > 0 ? ": hubwright proposals" : "")}.");

                // --strict (CI): a quote Microsoft no longer has is a failure; an unreachable page may be transient.
                return cli.Flag("strict") && results.Any(r => r.Status == DriftStatus.QuoteMissing) ? 1 : 0;
            }

            default:
                foreach (var skill in session.Skills.Skills)
                {
                    Console.WriteLine($"{skill.Id,-26} {skill.Version,-8} {skill.Layer,-12} {skill.Rules.Count,3} rules  {skill.Title}");
                }

                foreach (var o in session.Skills.Overrides)
                {
                    Console.WriteLine($"  {o.Id}: {o.To} v{o.ToVersion} replaces {o.From} v{o.FromVersion}{(o.Outdated ? " (older, review)" : "")}");
                }

                return 0;
        }
    }

    internal static int Proposals()
    {
        var session = Session();
        var id = cli.At(2);
        switch (cli.At(1)?.ToLowerInvariant())
        {
            case "show":
            {
                var p = session.Proposals.Find(id ?? "") ?? throw new InvalidOperationException($"No proposal {id}.");
                Console.WriteLine($"{p.Id} · {p.Kind} · {p.Status} · by {p.ProposedBy}\n{p.Title}\n\n{p.Summary}");
                foreach (var (name, content) in p.Files)
                {
                    Console.WriteLine($"\n--- {name} ---\n{content}");
                }

                return 0;
            }

            case "accept":
            case "reject":
            {
                var p = session.Proposals.Find(id ?? "") ?? throw new InvalidOperationException($"No proposal {id}.");
                if (p.Status != ProposalStatus.Pending)
                {
                    throw new InvalidOperationException($"Proposal {p.Id} is already {p.Status.ToString().ToLowerInvariant()}.");
                }

                var accept = cli.At(1)!.Equals("accept", StringComparison.OrdinalIgnoreCase);
                if (accept)
                {
                    switch (p.Kind)
                    {
                        case ProposalKind.Blueprint:
                            var path = new TenantStore(p.Target).SaveBlueprint(Blueprint.FromJson(p.Files["blueprint.json"]));
                            Console.WriteLine($"Blueprint saved: {path}");
                            break;
                        case ProposalKind.SiteDesign:
                            var design = SiteDesigns.Accept(session, p);
                            Console.WriteLine($"{design.Title} now follows the proposed design ({design.PatternName}); the build plan was updated if there is one.");
                            break;
                        case ProposalKind.SkillUpdate:
                            var skill = session.Skills.Find(p.Target) ?? throw new InvalidOperationException($"Skill {p.Target} is no longer installed.");
                            var dir = SkillRefresher.Fork(skill, DateOnly.FromDateTime(DateTime.UtcNow));
                            if (p.Files.TryGetValue("librarian.md", out var notes))
                            {
                                File.WriteAllText(Path.Combine(dir, "REVIEW.md"), notes);
                            }

                            Console.WriteLine($"Copied {skill.Id} to {dir} with a new version and today's review date. Apply the edits from REVIEW.md, then `hubwright skills validate`.");
                            break;
                    }
                }

                p.Status = accept ? ProposalStatus.Accepted : ProposalStatus.Rejected;
                p.DecidedBy = Environment.UserName;
                p.Decided = DateTimeOffset.UtcNow;
                session.Proposals.Save(p);
                session.Audit.Append($"proposal.{(accept ? "accept" : "reject")}", $"{p.Id} {p.Title}");
                Console.WriteLine($"Proposal {p.Id} {p.Status.ToString().ToLowerInvariant()}.");
                return 0;
            }

            default:
                foreach (var p in session.Proposals.All())
                {
                    Console.WriteLine($"{p.Id}  {p.Status,-9} {p.Kind,-12} {p.Title}");
                }

                return 0;
        }
    }
}
