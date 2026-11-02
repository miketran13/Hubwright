using Hubwright.Agents;
using Hubwright.Engine;
using Hubwright.Proposals;
using Hubwright.Skills;
using Hubwright.Snapshot;
using Hubwright.Storage;
using Hubwright.Templates;

namespace Hubwright.Tests;

public class ProposalTests
{
    private static (IntranetProposal Proposal, ProposalSeed Seed, Workspace Workspace) Fabrikam(ProposalApproach approach = ProposalApproach.Balanced)
    {
        var home = Directory.CreateTempSubdirectory("hubwright-proposal").FullName;
        var store = new TenantStore("fabrikam", home);
        store.SaveSnapshot(SampleTenants.Fabrikam());
        var skills = SkillTests.BuiltIn();
        var workspace = new Workspace(store, skills);
        var current = workspace.RequireAssessment();
        var seed = IntranetProposalGenerator.Seed(current.Snapshot);
        seed.Answers.Approach = approach;
        var proposal = IntranetProposalGenerator.Generate(current.Snapshot, current, seed.Answers, skills.Templates, [], seed.Observations);
        return (proposal, seed, workspace);
    }

    [Fact]
    public void The_catalogue_has_every_Microsoft_template_and_look_book_package_and_is_valid()
    {
        var skills = SkillTests.BuiltIn();
        var templates = skills.Templates;
        Assert.Equal(25, templates.Count(t => t.Source == TemplateSource.Microsoft));
        Assert.Equal(30, templates.Count(t => t.Source == TemplateSource.PnpPackage));
        Assert.All(templates, t => Assert.False(string.IsNullOrWhiteSpace(t.Quote)));
        Assert.All(templates.Where(t => t.Source == TemplateSource.PnpPackage), t => Assert.EndsWith(".pnp", t.Package, StringComparison.Ordinal));
        Assert.Equal("96c933ac-3698-44c7-9f4a-5fd17d71af9e", templates.Single(t => t.Id == "standard-communication").SiteDesignId);
        Assert.DoesNotContain(SkillValidator.Validate(skills, new DateOnly(2026, 10, 28)), i => i.Level == IssueLevel.Error);
    }

    [Theory]
    [InlineData("HR – Benefits", null, "human-resources")]
    [InlineData("IT – Service desk", null, "it-help-desk")]
    [InlineData("New starters", "Onboarding: first-week checklist", "new-employee-onboarding")]
    [InlineData("Fabrikam Home", "company news front door", "organization-home")]
    [InlineData("Project Apollo", null, "project-management")]
    public void Templates_are_suggested_from_the_site_title_first(string title, string? purpose, string expected)
        => Assert.Equal(expected, TemplateMatcher.Suggest(SkillTests.BuiltIn().Templates, title, purpose).First().Template.Id);

    [Fact]
    public void Team_sites_only_get_team_templates_and_packages_can_be_left_out()
    {
        var templates = SkillTests.BuiltIn().Templates;
        Assert.All(TemplateMatcher.Suggest(templates, "HR Team", "workspace", SiteKind.TeamGroup), m => Assert.NotEqual(SiteKind.Communication, m.Template.Kind));
        Assert.All(TemplateMatcher.Suggest(templates, "Benefits", packages: false), m => Assert.Equal(TemplateSource.Microsoft, m.Template.Source));
    }

    [Fact]
    public void The_seed_reads_functions_places_and_brand_from_the_scan()
    {
        var (_, seed, _) = Fabrikam();
        var answers = seed.Answers.Intranet;
        Assert.Equal("Fabrikam", answers.Organization);
        Assert.Contains("Sales", answers.Departments);
        Assert.Equal(["EMEA", "Nordics"], answers.Regions);
        Assert.Equal("#f2c811", answers.BrandColor);
        Assert.True(answers.ExternalCollaboration);
        Assert.Contains(seed.Observations, o => o.Contains("suggestions", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_existing_site_gets_a_plan_that_follows_the_guidance()
    {
        var (p, _, _) = Fabrikam();
        var snapshot = SampleTenants.Fabrikam();
        Assert.Equal(snapshot.Sites.Count, p.Sites.Count);

        // The Sales hub is kept and renamed; places stop being hubs and join the Locations hub.
        var sales = Assert.Single(p.Blueprint.Hubs, h => h.Title == "Sales Hub");
        Assert.NotNull(sales.ExistingUrl);
        Assert.Contains(SiteAction.RenameHub, p.Sites.Single(s => s.Title == "Sales Portal").Actions);
        var emea = p.Sites.Single(s => s.Title == "EMEA sales");
        Assert.Equal("Locations Hub", emea.TargetHub);
        Assert.Contains(SiteAction.StopBeingHub, emea.Actions);

        // The extranet leaves its hub; the classic root is retired in favour of a new home site.
        Assert.Contains(SiteAction.KeepOutOfHubs, p.Sites.Single(s => s.Title == "Partner portal").Actions);
        Assert.Equal("retire", p.Sites.Single(s => s.Title == "Fabrikam Intranet").Disposition);
        Assert.Null(p.Blueprint.Home.ExistingUrl);
        Assert.Equal("organization-home", p.NewSites.Single(n => n.Title == p.Blueprint.Home.Title).TemplateId);

        // Archived sites carry only the archive step (and closing open access), nothing else.
        Assert.All(p.Sites.Where(s => s.Disposition == "archive"), s => Assert.All(s.Actions, a => Assert.True(a is SiteAction.Archive or SiteAction.ReviewPermissions)));
        Assert.All(p.NewSites, n => Assert.NotNull(n.TemplateId));
    }

    [Fact]
    public void Keep_what_works_archives_nothing_and_renames_nothing()
    {
        var (p, _, _) = Fabrikam(ProposalApproach.KeepWhatWorks);
        Assert.DoesNotContain(p.Sites, s => s.Disposition == "archive");
        Assert.DoesNotContain(p.Sites, s => s.Actions.Contains(SiteAction.RenameHub));
    }

    [Fact]
    public void The_target_scores_better_and_the_roadmap_cites_only_relevant_rules()
    {
        var (p, _, workspace) = Fabrikam();
        var target = workspace.Assess(p.Blueprint.ToSnapshot());
        Assert.True(target.Overall > p.ScoreBefore);
        Assert.Equal(4, p.Phases.Count);
        var associate = p.Phases.SelectMany(ph => ph.Items).Where(i => i.Text.StartsWith("Associate", StringComparison.Ordinal));
        Assert.All(associate, i => Assert.All(i.Rules, r => Assert.True(r.StartsWith("ia.", StringComparison.Ordinal) || r.StartsWith("hubs.", StringComparison.Ordinal))));
    }

    [Fact]
    public void The_proposal_page_has_every_section_and_round_trips()
    {
        var (p, _, workspace) = Fabrikam();
        var html = ProposalWriter.Html(p, workspace.Skills.Templates);
        foreach (var id in new[] { "summary", "found", "structure", "sites", "new", "look", "policies", "roadmap", "decisions", "sources" })
        {
            Assert.Contains($"id=\"{id}\"", html, StringComparison.Ordinal);
        }

        Assert.Contains("<iframe", html, StringComparison.Ordinal);
        var copy = IntranetProposal.FromJson(p.ToJson());
        Assert.Equal(p.Sites.Count, copy.Sites.Count);
        Assert.Contains("## Roadmap", ProposalWriter.Markdown(copy), StringComparison.Ordinal);
    }
}
