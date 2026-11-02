using Hubwright.Agents;
using Hubwright.Designs;
using Hubwright.Engine;

namespace Hubwright.Cli;

/// <summary>Site designs (the desktop's Site designs page) and the scope and permissions explanation (Home).</summary>
internal static partial class Commands
{
    internal static async Task<int> DesignAsync()
    {
        var session = Session();
        var (folder, proposal) = SiteDesigns.Latest(session);
        var site = cli.At(2);
        string Site() => site ?? throw new InvalidOperationException($"Usage: hubwright design {cli.At(1)} \"<site title or url>\"");
        switch (cli.At(1)?.ToLowerInvariant())
        {
            case "show":
                Console.WriteLine(SiteDesignProposals.Describe(SiteDesigns.Require(proposal, Site())));
                return 0;

            case "preview":
            {
                var design = SiteDesigns.Require(proposal, Site());
                var path = Path.Combine(folder, "design-preview.html");
                File.WriteAllText(path, SiteDesigns.Preview(session, proposal, design.Url));
                Console.WriteLine($"{path}\nHubwright's wireframe with sample content; nothing comes from or goes to the tenant.");
                Open(path);
                return 0;
            }

            case "patterns":
                foreach (var p in session.Skills.Patterns)
                {
                    Console.WriteLine($"{p.Id,-22} {p.Name} · {p.Purpose}");
                }

                return 0;

            case "pattern":
            {
                var pattern = cli.At(3) ?? throw new InvalidOperationException("Usage: hubwright design pattern \"<site>\" <pattern id>  (hubwright design patterns lists them)");
                var design = SiteDesigns.UsePattern(session, folder, proposal, Site(), pattern);
                Console.WriteLine($"{design.Title} now follows {design.PatternName}. The build plan was updated if there is one.");
                return 0;
            }

            case "include":
            case "exclude":
            {
                var design = SiteDesigns.Include(session, folder, proposal, Site(), cli.At(1)!.Equals("include", StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"{design.Title}: {(design.Apply ? "the build drafts its designed page (Site design steps)" : "left out of the build")}.");
                return 0;
            }

            case "open":
            {
                var design = SiteDesigns.Require(proposal, Site());
                var url = cli.Flag("draft") ? SiteDesigns.DraftUrl(design) : design.Url;
                Console.WriteLine(design.IsNew && SiteDesigns.Progress(folder, design) is not { } ? $"{url}\n(A new site: it exists once its Build step ran.)" : url);
                Open(url);
                return 0;
            }

            case "apply":
            {
                var publish = cli.Flag("publish");
                var before = SiteDesigns.Require(proposal, Site());
                var (wasApplied, wasPublished) = (before.Apply, before.Publish);
                var (plan, steps) = SiteDesigns.Prepare(session, folder, proposal, Site(), publish);
                var design = SiteDesigns.Require(proposal, Site());
                var warning = publish
                    ? $"""

                      Publishing makes the drafted page the home page of {design.Title}: everyone who opens the site sees it,
                      placeholders included unless someone replaced them in {SiteDesigns.DraftUrl(design)}.
                      Tell the site's members first. The current home page is recorded and kept; nothing is deleted.
                      """
                    : $"\nA draft only: nobody sees it until it is published. Then: hubwright design apply \"{design.Title}\" --publish";
                int code;
                try
                {
                    code = await ConfirmAndRunAsync(session, plan, folder, steps, warning);
                }
                finally
                {
                    // Cancelled (or stopped) before anything ran: the design goes back to what it was.
                    if (steps.All(id => plan.Find(id)?.Status == Hubwright.Implementation.StepStatus.Pending))
                    {
                        SiteDesigns.Restore(session, folder, proposal, design.Url, wasApplied, wasPublished);
                    }
                }

                Console.WriteLine(SiteDesigns.Progress(folder, design));
                return code;
            }

            case "ask":
            {
                var request = cli.At(3) ?? throw new InvalidOperationException("Usage: hubwright design ask \"<site>\" \"<what to change>\"");
                Console.WriteLine(await SiteDesigns.AskAsync(session, Site(), request));
                Console.WriteLine("\nSuggestions wait for you: `hubwright proposals`, then `hubwright proposals accept <id>`.");
                return 0;
            }

            default:
                if (proposal.Designs.Count == 0)
                {
                    Console.WriteLine("This proposal has no site designs (it was made before the site-design skill). Run `hubwright proposal new`.");
                    return 0;
                }

                foreach (var d in proposal.Designs)
                {
                    var state = d.Blocked is not null ? "blocked" : d.Apply ? "in build" : "";
                    Console.WriteLine($"{(d.Confident ? " " : "?")} {d.Title,-36} {d.PatternName,-28} {state,-9} {d.Url}");
                }

                Console.WriteLine("\n? = purpose guessed from the kind of site only: confirm it. `hubwright design show \"<site>\"` explains a design.");
                return 0;
        }
    }

    internal static int Scope()
    {
        Console.WriteLine(Awareness.Markdown());
        if (cli.Flag("read"))
        {
            Awareness.Acknowledge(Session());
            Console.WriteLine("Recorded that you have read this.");
        }
        else if (Awareness.ReadAt() is null)
        {
            Console.WriteLine("Run `hubwright scope --read` to record that you have read it.");
        }

        return 0;
    }
}
