using Hubwright.Agents;
using Hubwright.Engine;
using Hubwright.Hints;
using Hubwright.Implementation;
using Hubwright.Preview;
using Hubwright.Proposals;
using Hubwright.Storage;
using Hubwright.Theming;

namespace Hubwright.Tests;

public class PreviewAndThemeTests
{
    private static (IntranetProposal Proposal, ImplementationPlan Plan, HintBook Hints, Snapshot.TenantSnapshot Today) Fabrikam()
    {
        var store = new TenantStore("fabrikam", Directory.CreateTempSubdirectory("hubwright-preview").FullName);
        store.SaveSnapshot(SampleTenants.Fabrikam());
        var skills = SkillTests.BuiltIn();
        var current = new Workspace(store, skills).RequireAssessment();
        var seed = IntranetProposalGenerator.Seed(current.Snapshot);
        var proposal = IntranetProposalGenerator.Generate(current.Snapshot, current, seed.Answers, skills.Templates, []);
        return (proposal, ImplementationPlanner.Build(proposal, current.Snapshot, skills.Templates), skills.HintBook(), current.Snapshot);
    }

    [Fact]
    public void Every_step_and_every_structural_suggestion_has_an_explanation()
    {
        var (proposal, plan, hints, _) = Fabrikam();
        Assert.All(plan.Steps, s => Assert.NotNull(hints.For(s)));
        Assert.All(plan.Steps.Where(s => s.Hint is not null), s => Assert.True(hints.Find(s.Hint) is not null, $"hint {s.Hint} for {s.Id} is missing"));
        Assert.All(proposal.Phases.SelectMany(p => p.Items).Where(i => i.Hint is not null), i => Assert.True(hints.Find(i.Hint) is not null, $"hint {i.Hint} is missing"));
        Assert.All(Enum.GetValues<SiteAction>().Select(HintBook.ActionHint).OfType<string>(), id => Assert.NotNull(hints.Find(id)));

        var hub = hints.Find("register-hub")!;
        Assert.NotEmpty(hub.How);
        Assert.NotNull(hub.Example);
        Assert.Contains(hints.Evidence(hub), e => e.Source.StartsWith("https://learn.microsoft.com/", StringComparison.Ordinal));
        Assert.Contains(hints.Evidence(hints.Find("site-logo")!), e => e.Quote.Contains("64 x 64", StringComparison.Ordinal));
    }

    [Fact]
    public void The_structure_preview_places_every_site_and_shows_progress()
    {
        var (proposal, plan, hints, today) = Fabrikam();
        plan.Find("theme")!.Status = StepStatus.Done;
        var hr = plan.Steps.First(s => s.Operation == StepOperation.CreateSite);
        plan.Steps.Where(s => s.Target == hr.Target).ToList().ForEach(s => s.Status = StepStatus.Done);
        var html = StructurePreview.Render(proposal, today, hints, Implementations.SiteStatuses(plan));
        Assert.Contains("const D=", html, StringComparison.Ordinal);
        Assert.All(proposal.Sites, s => Assert.Contains(System.Text.Json.JsonSerializer.Serialize(s.Url.TrimEnd('/')).Trim('"'), html, StringComparison.Ordinal));
        Assert.Contains("Locations Hub", html, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"Done\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("</script><", html[..html.LastIndexOf("</script>", StringComparison.Ordinal)], StringComparison.Ordinal);

        var page = ProposalWriter.Html(proposal, SkillTests.BuiltIn().Templates, hints, today);
        Assert.Contains("class=\"structure\"", page, StringComparison.Ordinal);
        Assert.Contains("<details class=\"hint\">", page, StringComparison.Ordinal);
    }

    [Fact]
    public void The_site_preview_has_a_page_for_every_proposed_site_with_its_navigation()
    {
        var (proposal, plan, _, _) = Fabrikam();
        var html = SitePrototype.Render(proposal, SkillTests.BuiltIn().Templates, "assets/logo.png", Implementations.SiteStatuses(plan));
        var json = html[(html.IndexOf("const D=", StringComparison.Ordinal) + 8)..html.IndexOf(";const S=", StringComparison.Ordinal)];
        var data = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var keys = data["sites"]!.AsArray().Select(s => s!["key"]!.GetValue<string>()).ToHashSet();
        var b = proposal.Blueprint;
        Assert.Contains(SitePrototype.Key(b.Url(b.Home.Slug)), keys);
        Assert.All(b.Hubs, h => Assert.Contains(SitePrototype.Key(h.ExistingUrl ?? b.Url(h.Slug)), keys));
        Assert.All(b.Hubs.SelectMany(h => h.Sites), s => Assert.Contains(SitePrototype.Key(s.ExistingUrl ?? b.Url(s.Slug)), keys));
        Assert.All(proposal.Sites.Where(s => s.TargetHub is not null), s => Assert.Contains(SitePrototype.Key(s.Url), keys));

        // The kept Sales hub answers to its planned URL too, so hub menus built from the plan still lead somewhere.
        var sales = b.Hubs.Single(h => h.Title == "Sales Hub");
        Assert.Equal(SitePrototype.Key(sales.ExistingUrl!), data["aliases"]![SitePrototype.Key(b.Url(sales.Slug))]!.GetValue<string>());
        Assert.NotEmpty(data["global"]!.AsArray());
        Assert.Equal("assets/logo.png", data["logo"]!.GetValue<string>());
        Assert.Contains(data["sites"]!.AsArray(), s => s!["status"] is not null);
    }

    [Fact]
    public void Theme_suggestions_are_all_accessible_and_come_from_the_logo()
    {
        var teal = new Rgb(0x00, 0xa3, 0x96);
        var suggestions = ThemeSuggestions.Suggest("Contoso", Rgb.Parse("#f2c811"), [teal]);
        Assert.Contains(suggestions, s => s.Label == "Logo");
        Assert.Contains(suggestions, s => s.Background.IsDark);
        Assert.All(suggestions, s => Assert.True(s.Theme.Accessible, $"{s.Label} fails contrast"));
        Assert.Contains("Adjusted from #f2c811", suggestions.Single(s => s.Label == "Brand").Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Logo_colours_skip_white_transparent_and_grey_pixels()
    {
        const int w = 40, h = 20;
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var x = i % w;
            (byte b, byte g, byte r, byte a) = x < 10 ? ((byte)0xbd, (byte)0x6c, (byte)0x0f, (byte)255) // blue
                : x < 15 ? ((byte)0x11, (byte)0xc8, (byte)0xf2, (byte)255)                              // yellow
                : x < 30 ? ((byte)255, (byte)255, (byte)255, (byte)255)                                  // white
                : ((byte)0, (byte)0, (byte)0, (byte)0);                                                 // transparent
            pixels[(i * 4) + 0] = b;
            pixels[(i * 4) + 1] = g;
            pixels[(i * 4) + 2] = r;
            pixels[(i * 4) + 3] = a;
        }

        var colors = ThemeSuggestions.LogoColors(pixels, w, h);
        Assert.Equal(2, colors.Count);
        Assert.Equal("#0f6cbd", colors[0].Hex);
    }

    [Theory]
    [InlineData(192, 64, true, true)]
    [InlineData(64, 64, true, true)]
    [InlineData(400, 50, false, false)]
    [InlineData(40, 120, false, true)]
    public void Logo_checks_follow_Microsofts_header_sizes(int width, int height, bool shapeOk, bool heightOk)
    {
        var checks = ThemeSuggestions.CheckLogo(width, height, 20_000, hasTransparency: true, ".png");
        Assert.Equal(shapeOk, checks[0].Passes);
        Assert.Equal(heightOk, checks[1].Passes);
        Assert.Equal(width == height, checks[2].Passes);
        Assert.Equal(ThemeSuggestions.LogoSource, checks[0].Source);
    }

    [Fact]
    public void An_edited_palette_and_logo_reach_the_mockup_and_the_theme_steps()
    {
        var (proposal, plan, _, _) = Fabrikam();
        var b = proposal.Blueprint;
        b.Palette = new Dictionary<string, string>(b.Theme().Palette) { ["themePrimary"] = "#5b2c83" };
        b.ThemeName = "Fabrikam violet";
        b.Logo = "assets/logo.png";
        Assert.Equal("#5b2c83", b.Theme().Palette["themePrimary"]);
        Assert.Equal("Fabrikam violet", b.Theme().Name);
        Assert.Contains("src=\"assets/logo.png\"", MockupRenderer.Render(MockupModel.FromBlueprint(b)), StringComparison.Ordinal);
        Assert.Contains("#5b2c83", ImplementationPlanner.ThemeJson(b), StringComparison.Ordinal);
        Assert.Contains(plan.Steps, s => s.Operation == StepOperation.SetSiteLogo && s.Kind == StepKind.Automatic);
    }
}
