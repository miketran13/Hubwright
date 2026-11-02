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

/// <summary>Stage 3, Plan: blueprints, intranet proposals, site templates, themes and previews.</summary>
internal static partial class Commands
{
    internal static async Task<int> BlueprintAsync()
    {
        var session = Session();
        switch (cli.At(1)?.ToLowerInvariant())
        {
            case "new":
            {
                var answers = cli.Option("answers") is { } file
                    ? JsonSerializer.Deserialize<IntranetAnswers>(File.ReadAllText(file), TenantSnapshot.Json) ?? new IntranetAnswers()
                    : Questionnaire(session);
                var blueprint = BlueprintGenerator.Generate(answers);
                if (cli.Flag("ai"))
                {
                    Console.Error.WriteLine("  asking the architect to review it…");
                    var path0 = session.Store.SaveBlueprint(blueprint);
                    Console.WriteLine(await session.Runner.RunAsync(Roles.Architect, "Review the latest blueprint with check_blueprint and get_blueprint. Suggest improvements; if they are substantial, file them with propose_blueprint."));
                    Console.WriteLine($"Blueprint saved: {path0}");
                    return 0;
                }

                var path = session.Store.SaveBlueprint(blueprint);
                session.Audit.Append("blueprint.new", path);
                var check = session.Workspace.Assess(blueprint.ToSnapshot());
                Console.WriteLine($"Blueprint '{blueprint.Name}': {blueprint.Hubs.Count} hubs, {blueprint.Hubs.Sum(h => h.Sites.Count) + blueprint.Standalone.Count + 1} sites · checks {Score(check.Overall)} · {path}");
                foreach (var reason in blueprint.Rationale)
                {
                    Console.WriteLine("  · " + reason);
                }

                Console.WriteLine($"Next: hubwright preview --tenant {session.Store.Tenant} --open   ·   hubwright blueprint export --tenant {session.Store.Tenant}");
                return 0;
            }

            case "check":
            {
                var blueprint = session.Workspace.Blueprint ?? throw new InvalidOperationException("No blueprint yet. Run `hubwright blueprint new`.");
                Console.WriteLine(ReportWriter.Markdown(session.Workspace.Assess(blueprint.ToSnapshot())));
                return 0;
            }

            case "export":
            {
                var blueprint = session.Workspace.Blueprint ?? throw new InvalidOperationException("No blueprint yet. Run `hubwright blueprint new`.");
                var dir = Directory.CreateDirectory(cli.Option("out") ?? Path.Combine(session.Store.Directory, "export")).FullName;
                File.WriteAllText(Path.Combine(dir, "provision.ps1"), Exporters.ProvisioningScript(blueprint));
                File.WriteAllText(Path.Combine(dir, "site-script.json"), Exporters.SiteScript(blueprint));
                File.WriteAllText(Path.Combine(dir, "theme.ps1"), Exporters.ThemeScript(blueprint.Theme()));
                File.WriteAllText(Path.Combine(dir, "theme.json"), blueprint.Theme().ToJson());
                File.WriteAllText(Path.Combine(dir, "mockup.html"), MockupRenderer.Render(MockupModel.FromBlueprint(blueprint)));
                session.Audit.Append("blueprint.export", dir);
                Console.WriteLine($"Exported to {dir}: provision.ps1, site-script.json, theme.ps1, theme.json, mockup.html. Review before running anything.");
                return 0;
            }

            default:
                Console.WriteLine(session.Workspace.Blueprint?.ToJson() ?? "No blueprint yet. Run `hubwright blueprint new`.");
                return 0;
        }
    }

    internal static IntranetAnswers Questionnaire(HubwrightSession session)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("Pass --answers answers.json when input is not a terminal (see docs/getting-started.md for the format).");
        }

        string Ask(string question, string fallback)
        {
            Console.Write($"{question} [{fallback}]: ");
            var answer = Console.ReadLine();
            return string.IsNullOrWhiteSpace(answer) ? fallback : answer.Trim();
        }

        List<string> AskList(string question, string fallback)
            => [.. Ask(question, fallback).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        Console.WriteLine("🏗️ Let's design your intranet. Press Enter to keep a default.");
        var answers = new IntranetAnswers
        {
            Organization = Ask("Organization name", "Contoso"),
            TenantPrefix = Ask("Tenant prefix (contoso for contoso.sharepoint.com)", session.Config.Tenant.Name ?? "contoso"),
            Employees = int.TryParse(Ask("How many employees", "500"), out var n) ? n : 500,
            Departments = AskList("Departments, comma-separated", "HR, IT, Finance, Communications, Sales"),
            Regions = AskList("Offices or regions (empty for none)", ""),
            Languages = AskList("Languages", "en-US"),
            Goals = AskList("Goals: news, policies, onboarding, collaboration, communities, copilot", "news, policies, onboarding"),
            ExternalCollaboration = Ask("Work with partners or guests? (y/n)", "n").StartsWith('y'),
            AllowAnyoneLinks = Ask("Allow Anyone (unauthenticated) links? (y/n)", "n").StartsWith('y'),
            BrandColor = Ask("Brand colour", "#0f6cbd"),
            HeaderLayout = Ask("Header layout: minimal, compact, standard, extended", "standard"),
        };
        foreach (var question in session.Skills.Skills.SelectMany(s => s.Questions))
        {
            Console.WriteLine($"  To decide: {question.Text} ({question.Why})");
        }

        return answers;
    }

    internal static async Task<int> ProposalAsync()
    {
        var session = Session();
        switch (cli.At(1)?.ToLowerInvariant())
        {
            case "answers":
            {
                var seed = IntranetProposals.Seed(session);
                seed.Answers.Decisions = IntranetProposals.Questions(session.Skills).Where(d => d.Answer is not null).ToDictionary(d => d.Id, d => d.Answer!, StringComparer.OrdinalIgnoreCase);
                var json = seed.Answers.ToJson();
                if (cli.Option("out") is { } file)
                {
                    File.WriteAllText(file, json);
                    Console.WriteLine($"Wrote {file}. Edit it, then: hubwright proposal new --answers {file}");
                }
                else
                {
                    Console.WriteLine(json);
                }

                foreach (var note in seed.Observations)
                {
                    Console.Error.WriteLine("  · " + note);
                }

                return 0;
            }

            case "new":
            {
                var seed = IntranetProposals.Seed(session);
                var answers = cli.Option("answers") is { } file ? ProposalAnswers.FromJson(File.ReadAllText(file)) : ProposalQuestionnaire(session, seed);
                if (cli.Option("approach") is { } approach)
                {
                    answers.Approach = approach.ToLowerInvariant() switch
                    {
                        "keep" => ProposalApproach.KeepWhatWorks,
                        "restructure" => ProposalApproach.Restructure,
                        _ => ProposalApproach.Balanced,
                    };
                }

                var proposal = IntranetProposals.Build(session, answers, seed.Observations);
                if (cli.Flag("ai"))
                {
                    Console.Error.WriteLine($"  asking the architect for the summary ({session.Config.Ai.BackendFor(Roles.Architect)})…");
                    proposal.Narrative = await IntranetProposals.NarrateAsync(session, proposal);
                }

                var folder = IntranetProposals.Save(session, proposal);
                Console.WriteLine($"{proposal.Name}: score {Score(proposal.ScoreBefore)} today → {Score(proposal.ScoreAfter)} for the target design.");
                Console.WriteLine($"  {proposal.Blueprint.Hubs.Count} hubs ({proposal.Blueprint.Hubs.Count(h => h.ExistingUrl is not null)} kept) · {proposal.NewSites.Count} new sites from templates · {proposal.Sites.Count} existing sites placed: {string.Join(", ", proposal.Sites.GroupBy(s => s.Disposition).Select(g => $"{g.Count()} {g.Key}"))}");
                Console.WriteLine($"  {Path.Combine(folder, "proposal.html")}");
                Console.WriteLine($"Next: hubwright proposal use   (make it the blueprint, then preview and export)");
                if (cli.Flag("open"))
                {
                    Process.Start(new ProcessStartInfo(Path.Combine(folder, "proposal.html")) { UseShellExecute = true })?.Dispose();
                }

                return 0;
            }

            case "structure":
            {
                var folders = session.Store.IntranetProposals();
                var index = int.TryParse(cli.At(2), out var n) ? n - 1 : 0;
                var folder = index >= 0 && index < folders.Count ? folders[index] : throw new InvalidOperationException("No such proposal. Run `hubwright proposal list`.");
                var proposal = TenantStore.LoadIntranetProposal(folder);
                var path = cli.Option("out") ?? Path.Combine(folder, "structure.html");
                var plan = File.Exists(Path.Combine(folder, Implementations.FileName)) ? Implementations.For(session, folder) : null;
                File.WriteAllText(path, StructurePreview.Render(proposal, session.Workspace.Snapshot, session.Skills.HintBook(), plan is null ? null : Implementations.SiteStatuses(plan)));
                Console.WriteLine($"Structure preview: {Path.GetFullPath(path)}");
                Open(path);
                return 0;
            }

            case "sites":
            {
                var folders = session.Store.IntranetProposals();
                var index = int.TryParse(cli.At(2), out var n) ? n - 1 : 0;
                var folder = index >= 0 && index < folders.Count ? folders[index] : throw new InvalidOperationException("No such proposal. Run `hubwright proposal list`.");
                var proposal = TenantStore.LoadIntranetProposal(folder);
                var path = cli.Option("out") ?? Path.Combine(folder, "site-preview.html");
                var plan = File.Exists(Path.Combine(folder, Implementations.FileName)) ? Implementations.For(session, folder) : null;
                File.WriteAllText(path, SitePrototype.Render(proposal, session.Skills.Templates, proposal.Blueprint.Logo, plan is null ? null : Implementations.SiteStatuses(plan)));
                Console.WriteLine($"Site preview: {Path.GetFullPath(path)} ({proposal.Blueprint.Hubs.Count} hubs, every site with sample content)");
                Open(path);
                return 0;
            }

            case "show":
            case "use":
            {
                var folders = session.Store.IntranetProposals();
                var index = int.TryParse(cli.At(2), out var n) ? n - 1 : 0;
                var folder = index >= 0 && index < folders.Count ? folders[index] : throw new InvalidOperationException("No such proposal. Run `hubwright proposal list`.");
                var proposal = TenantStore.LoadIntranetProposal(folder);
                if (cli.At(1)!.Equals("use", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"Blueprint saved: {IntranetProposals.UseAsBlueprint(session, proposal)}. Next: hubwright preview --open · hubwright blueprint export");
                    return 0;
                }

                var html = (cli.Option("format") ?? "md").Equals("html", StringComparison.OrdinalIgnoreCase);
                var file = Path.Combine(folder, html ? "proposal.html" : "proposal.md");
                if (cli.Flag("open"))
                {
                    Process.Start(new ProcessStartInfo(file) { UseShellExecute = true })?.Dispose();
                }
                else
                {
                    Console.WriteLine(html ? file : File.ReadAllText(file));
                }

                return 0;
            }

            default:
            {
                var folders = session.Store.IntranetProposals();
                for (var i = 0; i < folders.Count; i++)
                {
                    var p = TenantStore.LoadIntranetProposal(folders[i]);
                    Console.WriteLine($"{i + 1,3}  {p.Created:yyyy-MM-dd HH:mm}  {p.Answers.Approach,-13} {Score(p.ScoreBefore),3} → {Score(p.ScoreAfter),-3} {p.Name}");
                }

                if (folders.Count == 0)
                {
                    Console.WriteLine("No proposals yet. Run `hubwright proposal new` after a scan.");
                }

                return 0;
            }
        }
    }

    internal static ProposalAnswers ProposalQuestionnaire(HubwrightSession session, ProposalSeed seed)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("Pass --answers answers.json when input is not a terminal (`hubwright proposal answers --out answers.json` writes one to edit).");
        }

        string Ask(string question, string fallback)
        {
            Console.Write($"{question} [{fallback}]: ");
            var answer = Console.ReadLine();
            return string.IsNullOrWhiteSpace(answer) ? fallback : answer.Trim();
        }

        List<string> AskList(string question, IEnumerable<string> fallback)
            => [.. Ask(question, string.Join(", ", fallback)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        var a = seed.Answers;
        var i = a.Intranet;
        Console.WriteLine($"🏗️ An intranet proposal for {session.Store.Tenant}. What the scan told us:");
        foreach (var note in seed.Observations)
        {
            Console.WriteLine("   · " + note);
        }

        Console.WriteLine("Press Enter to keep a suggestion.\n");
        i.Organization = Ask("Organization name", i.Organization);
        i.Employees = int.TryParse(Ask("How many employees", i.Employees.ToString(CultureInfo.InvariantCulture)), out var e) ? e : i.Employees;
        i.Departments = AskList("Functions that get a hub, comma-separated", i.Departments);
        i.Regions = AskList("Offices or regions (empty for none)", i.Regions);
        i.Languages = AskList("Languages", i.Languages);
        i.Goals = AskList("Goals: news, policies, onboarding, collaboration, communities, copilot", i.Goals);
        i.ExternalCollaboration = Ask("Work with partners or guests? (y/n)", i.ExternalCollaboration ? "y" : "n").StartsWith('y');
        i.AllowAnyoneLinks = Ask("Allow Anyone (unauthenticated) links? (y/n)", i.AllowAnyoneLinks ? "y" : "n").StartsWith('y');
        i.BrandColor = Ask("Brand colour", i.BrandColor);
        i.HeaderLayout = Ask("Header layout: minimal, compact, standard, extended", i.HeaderLayout);
        a.Approach = Ask("Approach: keep (what works), balanced, restructure", "balanced").ToLowerInvariant() switch
        {
            "keep" => ProposalApproach.KeepWhatWorks,
            "restructure" => ProposalApproach.Restructure,
            _ => ProposalApproach.Balanced,
        };
        a.UsePackages = Ask("Also suggest look book PnP packages (needs PnP PowerShell)? (y/n)", "y").StartsWith('y');
        Console.WriteLine("\nDecisions (from the skills):");
        foreach (var d in IntranetProposals.Questions(session.Skills))
        {
            var choices = IntranetProposals.Choices(session.Skills, d.Id);
            Console.WriteLine($"  {d.Question}{(d.Why is null ? "" : $"\n    ({d.Why})")}");
            if (choices.Count > 0)
            {
                for (var c = 0; c < choices.Count; c++)
                {
                    Console.WriteLine($"    {c + 1}. {choices[c]}");
                }
            }

            var answer = Ask("   ", d.Answer ?? "");
            a.Decisions[d.Id] = int.TryParse(answer, out var pick) && pick >= 1 && pick <= choices.Count ? choices[pick - 1] : answer;
        }

        return a;
    }

    internal static int Templates()
    {
        var session = Session();
        var catalog = session.Skills.Templates;
        switch (cli.At(1)?.ToLowerInvariant())
        {
            case "suggest":
            {
                var title = string.Join(' ', cli.Positional.Skip(2));
                var matches = Hubwright.Templates.TemplateMatcher.Suggest(catalog, title, cli.Option("purpose"));
                foreach (var m in matches)
                {
                    Console.WriteLine($"{m.Template.Name,-48} {(m.Template.Source == Hubwright.Templates.TemplateSource.Microsoft ? "built in" : "package"),-9} matched {string.Join(", ", m.Matched)}\n  {m.Template.HowToApply}");
                }

                if (matches.Count == 0)
                {
                    Console.WriteLine("Nothing specific fits: use Standard communication or Standard team.");
                }

                return 0;
            }

            case "gallery":
            {
                var theme = session.Workspace.Blueprint?.Theme() ?? ThemeGenerator.Generate(new ThemeInput("Preview", Rgb.Parse(cli.Option("primary") ?? "#0f6cbd")));
                var path = cli.Option("out") ?? Path.Combine(session.Store.Directory, "templates.html");
                File.WriteAllText(path, Hubwright.Templates.TemplateGallery.Render(catalog, theme));
                Console.WriteLine($"Gallery: {Path.GetFullPath(path)} ({catalog.Count} templates)");
                Open(path);
                return 0;
            }

            default:
            {
                var source = cli.Option("source");
                foreach (var t in catalog.Where(t => source is null || (source.StartsWith("pack", StringComparison.OrdinalIgnoreCase) ? t.Source == Hubwright.Templates.TemplateSource.PnpPackage : t.Source == Hubwright.Templates.TemplateSource.Microsoft)))
                {
                    Console.WriteLine($"{t.Id,-52} {(t.Source == Hubwright.Templates.TemplateSource.Microsoft ? "built in" : "package"),-9} {(t.Kind == SiteKind.Communication ? "comm" : "team"),-5} {t.Name}");
                }

                return 0;
            }
        }
    }

    internal static int Theme()
    {
        switch (cli.At(1)?.ToLowerInvariant())
        {
            case "check":
            {
                var theme = ThemeGenerator.FromJson(File.ReadAllText(cli.At(2) ?? throw new InvalidOperationException("Usage: hubwright theme check <theme.json>")));
                PrintChecks(theme);
                return theme.Accessible ? 0 : 3;
            }

            default:
            {
                var primary = Rgb.Parse(cli.Option("primary") ?? "#0f6cbd");
                var background = Rgb.Parse(cli.Option("background") ?? "#ffffff");
                var theme = ThemeGenerator.Generate(new ThemeInput(cli.Option("name") ?? "Hubwright theme", primary)
                {
                    Background = background,
                    Text = background.IsDark ? new Rgb(0xf3, 0xf2, 0xf1) : new Rgb(0x32, 0x31, 0x30),
                });
                if (cli.Option("out") is { } output)
                {
                    File.WriteAllText(output, theme.ToJson());
                    File.WriteAllText(Path.ChangeExtension(output, ".ps1"), theme.ToPowerShell(hideDefaultThemes: true));
                    Console.WriteLine($"Wrote {output} and {Path.ChangeExtension(output, ".ps1")}");
                }
                else
                {
                    Console.WriteLine(theme.ToJson());
                }

                PrintChecks(theme);
                if (!theme.Accessible && ThemeGenerator.SuggestAccessiblePrimary(primary, background) is { } better)
                {
                    Console.WriteLine($"Suggestion: {better.Hex} is the nearest brand colour that passes. Try: hubwright theme generate --primary {better.Hex}");
                }

                return 0;
            }
        }
    }

    internal static void PrintChecks(SharePointTheme theme)
    {
        foreach (var c in theme.Checks)
        {
            Console.WriteLine($"  {(c.Passes ? "✓" : "✗")} {c.Usage,-36} {c.Foreground} on {c.Background}  {c.Ratio,5:0.00}:1 (needs {c.Required:0.0})");
        }
    }

    internal static int Preview()
    {
        var session = Session();
        var from = (cli.Option("from") ?? (session.Workspace.Blueprint is null ? "snapshot" : "blueprint")).ToLowerInvariant();
        MockupModel model;
        if (from == "blueprint")
        {
            var blueprint = session.Workspace.Blueprint ?? throw new InvalidOperationException("No blueprint yet. Run `hubwright blueprint new`, or use --from snapshot.");
            if (cli.Option("primary") is { } primary)
            {
                blueprint.BrandColor = Rgb.Parse(primary).Hex;
            }

            if (cli.Option("layout") is { } layout)
            {
                blueprint.HeaderLayout = layout;
            }

            model = MockupModel.FromBlueprint(blueprint);
        }
        else
        {
            var snapshot = session.Workspace.Snapshot ?? throw new InvalidOperationException("No snapshot yet. Run `hubwright sample` or `hubwright scan`.");
            var theme = cli.Option("primary") is { } primary
                ? ThemeGenerator.Generate(new ThemeInput("Preview", Rgb.Parse(primary)))
                : snapshot.Themes.FirstOrDefault() is { } existing ? ThemeGenerator.FromJson(JsonSerializer.Serialize(new { name = existing.Name, palette = existing.Palette })) : ThemeGenerator.Generate(new ThemeInput("Default", Rgb.Parse("#0f6cbd")));
            model = MockupModel.FromSnapshot(snapshot, theme, cli.Option("layout") ?? "standard");
        }

        var output = cli.Option("out") ?? Path.Combine(session.Store.Directory, "mockup.html");
        File.WriteAllText(output, MockupRenderer.Render(model));
        Console.WriteLine($"Mockup: {Path.GetFullPath(output)} ({(model.Theme.Accessible ? "theme passes WCAG AA" : "theme has contrast failures, see the Theme tab")})");
        Open(output);
        return 0;
    }
}
