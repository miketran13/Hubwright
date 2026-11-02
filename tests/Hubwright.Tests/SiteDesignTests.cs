using Microsoft.Extensions.AI;
using Hubwright.Agents;
using Hubwright.Designs;
using Hubwright.Engine;
using Hubwright.Implementation;
using Hubwright.Proposals;
using Hubwright.Skills;
using Hubwright.Snapshot;
using Hubwright.Storage;
using Hubwright.Tools;

namespace Hubwright.Tests;

/// <summary>Site designs: chosen by purpose, explained, checked when an agent changes them, drafted and never published.</summary>
public class SiteDesignTests
{
    private static (TenantStore Store, SkillLibrary Skills, IntranetProposal Proposal) Fabrikam()
    {
        var store = new TenantStore("fabrikam", Directory.CreateTempSubdirectory("hubwright-design").FullName);
        store.SaveSnapshot(SampleTenants.Fabrikam());
        var skills = SkillTests.BuiltIn();
        var current = new Workspace(store, skills).RequireAssessment();
        var proposal = IntranetProposalGenerator.Generate(current.Snapshot, current, IntranetProposalGenerator.Seed(current.Snapshot).Answers, skills.Templates, [], patterns: skills.Patterns);
        return (store, skills, proposal);
    }

    [Fact]
    public void Patterns_are_valid_data_and_every_site_in_the_proposal_gets_an_explained_design()
    {
        var (_, skills, proposal) = Fabrikam();
        Assert.Contains(skills.Patterns, p => p.Id == "hr-services");
        Assert.All(new[] { "home", "hub", "communication", "team" }, role => Assert.Contains(skills.Patterns, p => p.Fallback == role));
        Assert.DoesNotContain(SkillValidator.Validate(skills, new DateOnly(2026, 10, 31)), i => i.Level == IssueLevel.Error && (i.SkillId == "site-design" || (i.RuleId?.StartsWith("pattern:", StringComparison.Ordinal) ?? false)));

        Assert.Single(proposal.Designs, d => d.Role == "home");
        Assert.Equal(proposal.Blueprint.Hubs.Count, proposal.Designs.Count(d => d.Role == "hub"));
        Assert.All(proposal.Sites, s => Assert.NotNull(SiteDesigner.Find(proposal, s.Url)));
        Assert.All(proposal.Designs, d =>
        {
            Assert.NotEmpty(d.Reasons);
            Assert.NotEmpty(d.Practices);
            Assert.Empty(SiteDesigner.Validate(d, skills.Templates));
        });

        // Home and hubs are drafted by default; nothing blocked is.
        Assert.All(proposal.Designs.Where(d => d.Role is "home" or "hub" && d.Blocked is null), d => Assert.True(d.Apply));
        Assert.All(proposal.Designs.Where(d => d.Blocked is not null), d => Assert.False(d.Apply));
        Assert.Contains(proposal.Phases.SelectMany(p => p.Items), i => i.Hint == "site-design");
    }

    [Fact]
    public void A_site_gets_the_pattern_its_title_points_to_or_a_general_one_to_confirm()
    {
        var patterns = SkillTests.BuiltIn().Patterns;
        Assert.Equal("hr-services", SiteDesigner.Match(patterns, "HR – Benefits", null, SiteKind.Communication).Pattern.Id);
        Assert.Equal("learning", SiteDesigner.Match(patterns, "HR – Learning", null, SiteKind.Communication).Pattern.Id);
        Assert.Equal("project-team", SiteDesigner.Match(patterns, "Project 7", null, SiteKind.TeamGroup).Pattern.Id);
        Assert.Equal("policies", SiteDesigner.Match(patterns, "Policies and procedures", null, SiteKind.Communication).Pattern.Id);
        var (pattern, matched) = SiteDesigner.Match(patterns, "Zebra", null, SiteKind.TeamGroup);
        Assert.Equal("team-collaboration", pattern.Id);
        Assert.Empty(matched);
    }

    [Fact]
    public void Designs_with_unknown_sections_wrong_templates_or_links_in_labels_are_refused()
    {
        var (_, skills, proposal) = Fabrikam();
        var good = proposal.Designs.First(d => d.Kind == SiteKind.Communication);
        Assert.Empty(SiteDesigner.Validate(good, skills.Templates));

        var bad = good.Copy();
        bad.Sections = ["hero", "carousel"];
        bad.Navigation = ["https://evil.example/x", new string('a', 60)];
        bad.TemplateId = "standard-team";
        bad.HeaderLayout = "huge";
        var errors = SiteDesigner.Validate(bad, skills.Templates);
        Assert.Contains(errors, e => e.Contains("carousel", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("evil.example", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("team site template", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Header layout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_agent_redesign_is_checked_filed_and_changes_nothing_until_accepted()
    {
        var (store, skills, proposal) = Fabrikam();
        var folder = store.SaveIntranetProposal(proposal, "<html></html>", "# proposal");
        var proposals = new ProposalStore(Path.Combine(store.Directory, "suggestions"));
        var tools = new ToolCatalog(new Workspace(store, skills), proposals, Roles.Architect).All().ToDictionary(t => t.Name);
        var site = proposal.Designs.First(d => d.Role == "hub");

        Assert.Contains("hr-services", (await tools["list_site_patterns"].InvokeAsync(new AIFunctionArguments()))?.ToString(), StringComparison.Ordinal);
        Assert.Contains(site.PatternName, (await tools["get_site_design"].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["site"] = site.Url })))?.ToString(), StringComparison.Ordinal);

        var refused = (await tools["propose_site_design"].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["site"] = site.Title, ["summary"] = "x", ["sections"] = "hero, marquee" })))?.ToString();
        Assert.StartsWith("Not filed", refused, StringComparison.Ordinal);
        Assert.Empty(proposals.All());

        var filed = (await tools["propose_site_design"].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["site"] = site.Title, ["summary"] = "Lead with self-service.", ["sections"] = "quicklinks, faq, news", ["navigation"] = "Forms, FAQ" })))?.ToString();
        Assert.StartsWith("Filed suggestion", filed, StringComparison.Ordinal);
        var suggestion = Assert.Single(proposals.All());
        Assert.Equal(ProposalKind.SiteDesign, suggestion.Kind);
        Assert.Equal(folder, SiteDesignProposals.FolderOf(store, suggestion));

        // Filing changed nothing: the saved proposal still has the rules' design.
        var saved = TenantStore.LoadIntranetProposal(folder);
        Assert.Equal(site.Sections, SiteDesigner.Find(saved, site.Url)!.Sections);

        // Accepting replaces the design; the preview and the build plan follow it.
        var proposed = SiteDesignProposals.Read(suggestion);
        Assert.Equal(DesignOrigin.Agent, proposed.Origin);
        Assert.Equal(site.Url, proposed.Url);
        SiteDesigner.Replace(saved, proposed);
        Assert.Contains("\"links\":[\"Forms\",\"FAQ\"]", SitePrototype.Render(saved, skills.Templates), StringComparison.Ordinal);
        var plan = ImplementationPlanner.Build(saved, new Workspace(store, skills).RequireAssessment().Snapshot, skills.Templates);
        var step = plan.Steps.Single(s => s.Operation == StepOperation.AddDesignPage && s.Target == site.Url);
        Assert.Equal("quicklinks,faq,news", step.Parameters["sections"]);
    }

    [Fact]
    public void The_build_drafts_the_page_without_publishing_and_the_owner_switches_it()
    {
        var (store, skills, proposal) = Fabrikam();
        var plan = ImplementationPlanner.Build(proposal, new Workspace(store, skills).RequireAssessment().Snapshot, skills.Templates);
        var drafts = plan.Steps.Where(s => s.Operation == StepOperation.AddDesignPage).ToList();
        Assert.Equal(proposal.Designs.Count(d => d.Apply), drafts.Count);
        var hints = skills.HintBook();
        foreach (var draft in drafts)
        {
            Assert.Equal(StepKind.Automatic, draft.Kind);
            Assert.Equal(StepRisk.Adds, draft.Risk);
            Assert.Equal("design-page", draft.Hint);
            Assert.NotNull(hints.Find(draft.Hint));
            Assert.NotNull(ImplementationPlanner.Fallback(plan, draft));
            var script = PnpScripts.Script(plan, draft);
            PnpShell.CheckCmdlets(script);
            Assert.DoesNotContain("-Publish", script, StringComparison.Ordinal);
            Assert.DoesNotContain("HomePage", script, StringComparison.Ordinal);
            Assert.Contains("Get-PnPPage -Identity 'Proposed-home'", script, StringComparison.Ordinal);
            Assert.Contains("Add-PnPPageSection", script, StringComparison.Ordinal);

            var publish = plan.Find("design-publish-" + draft.Id["design-".Length..])!;
            Assert.Equal(StepKind.Guided, publish.Kind);
            Assert.Equal("publish-design", publish.Hint);
            Assert.Contains(draft.Id, publish.DependsOn);
            Assert.Contains(publish.Instructions, i => i.Contains("tell the members", StringComparison.Ordinal));
            Assert.Contains(publish.Instructions, i => i.Contains("Make homepage", StringComparison.Ordinal));
        }

        // A new site's page waits for the site to exist.
        Assert.All(drafts.Where(d => proposal.Designs.Any(x => x.IsNew && x.Url == d.Target)), d => Assert.NotEmpty(d.DependsOn));
        Assert.Throws<InvalidOperationException>(() => PnpShell.CheckCmdlets("Remove-PnPPage -Identity 'Home'"));

        // Without Publish, nothing in the build publishes: the publish step is guided.
        Assert.DoesNotContain(plan.Steps, s => s.Operation == StepOperation.PublishDesignPage);
    }

    [Fact]
    public async Task Publish_is_an_automatic_step_only_when_chosen_and_records_the_old_home_page()
    {
        var (store, skills, proposal) = Fabrikam();
        var site = proposal.Designs.First(d => d.Role == "hub" && !d.IsNew && d.Blocked is null);
        site.Publish = true;
        var plan = ImplementationPlanner.Build(proposal, new Workspace(store, skills).RequireAssessment().Snapshot, skills.Templates);
        var publish = Assert.Single(plan.Steps, s => s.Operation == StepOperation.PublishDesignPage);
        Assert.Equal(StepKind.Automatic, publish.Kind);
        Assert.Equal(StepRisk.Changes, publish.Risk);
        Assert.Equal(site.Url, publish.Target);
        Assert.Equal(ImplementationPlanner.PnpSetHomePageDoc, publish.Source);
        Assert.Equal("publish-design", publish.Hint);
        Assert.Contains(publish.Instructions, i => i.Contains("Make homepage", StringComparison.Ordinal));
        Assert.Contains(publish.DependsOn, id => plan.Find(id)?.Operation == StepOperation.AddDesignPage);

        // Reads first: the draft must exist, the old home page is printed before anything changes.
        var script = PnpScripts.Script(plan, publish);
        PnpShell.CheckCmdlets(script);
        Assert.True(script.IndexOf("Get-PnPHomePage", StringComparison.Ordinal) < script.IndexOf("Set-PnPPage", StringComparison.Ordinal));
        Assert.True(script.IndexOf("Set-PnPPage -Identity 'Proposed-home' -Publish", StringComparison.Ordinal) < script.IndexOf("Set-PnPHomePage -RootFolderRelativeUrl 'SitePages/Proposed-home.aspx'", StringComparison.Ordinal));
        Assert.Contains(PnpScripts.PreviousMarker, script, StringComparison.Ordinal);
        Assert.Contains("throw", script, StringComparison.Ordinal);

        // The runner records the home page it replaced; a page already home is left alone.
        var audit = new AuditLog(Path.Combine(store.Directory, "audit.jsonl"));
        using var client = new HttpClient(new Refuse());
        ImplementationRunner Runner(string output) => new(plan, () => { }, new Http.TenantHttp((_, _) => Task.FromResult("t"), client), new Http.TenantWriter((_, _) => Task.FromResult("t"), client), audit)
        {
            Pnp = new FakePnp(output),
            Tokens = (_, _) => Task.FromResult("t"),
        };
        plan.Find(publish.DependsOn[0])!.Status = StepStatus.Done;
        await Runner($"{PnpScripts.PreviousMarker} SitePages/Home.aspx\n{PnpScripts.ResultMarker} Published Proposed-home and made it the home page.\n").RunAsync([publish.Id], null, CancellationToken.None);
        Assert.Equal(StepStatus.Done, publish.Status);
        Assert.Equal("SitePages/Home.aspx", publish.Outputs["previous"]);

        publish.Status = StepStatus.Pending;
        await Runner($"{PnpScripts.PreviousMarker} SitePages/Proposed-home.aspx\n{PnpScripts.ResultMarker} Already published and the home page.\n").RunAsync([publish.Id], null, CancellationToken.None);
        Assert.Equal(StepStatus.AlreadyDone, publish.Status);
    }

    private sealed class FakePnp(string output) : IPnpShell
    {
        public Task<PnpRun> RunAsync(string script, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
        {
            PnpShell.CheckCmdlets(script);
            return Task.FromResult(new PnpRun(0, output, ""));
        }
    }

    /// <summary>Publishing is PnP only: any HTTP request fails the test.</summary>
    private sealed class Refuse : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException($"Unexpected {request.Method} {request.RequestUri}");
    }

    [Fact]
    public void The_scope_explanation_lists_what_the_code_allows()
    {
        var text = Awareness.Markdown();
        Assert.All(Auth.TenantAuth.GraphScopes, s => Assert.Contains(s.Replace("https://graph.microsoft.com/", "", StringComparison.Ordinal), text, StringComparison.Ordinal));
        Assert.All(PnpScripts.Cmdlets.Where(c => c != "Connect-PnPOnline"), c => Assert.Contains(c, text, StringComparison.Ordinal));
        Assert.All(Http.TenantWriter.Operations, o => Assert.Contains(o, text, StringComparison.Ordinal));
        Assert.Contains("never deletes", text, StringComparison.Ordinal);
        Assert.Equal(4, Awareness.Summary.Count);
    }
}
