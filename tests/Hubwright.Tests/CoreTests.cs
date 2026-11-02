using Hubwright.Blueprints;
using Hubwright.Engine;
using Hubwright.Rules;
using Hubwright.Scoring;
using Hubwright.Skills;
using Hubwright.Snapshot;
using Hubwright.Theming;

namespace Hubwright.Tests;

internal sealed class DictFacts(Dictionary<string, object?> facts, Dictionary<string, object?>? parameters = null) : IFactSource
{
    public object? Get(string name) => facts.TryGetValue(name, out var v) ? v ?? Expression.Unknown : throw new RuleException($"no fact {name}");

    public object? Parameter(string name) => parameters?[name];
}

public class ExpressionTests
{
    private static bool? Test(string text, Dictionary<string, object?> facts, Dictionary<string, object?>? p = null)
        => Expression.Parse(text).Test(new DictFacts(facts, p));

    [Fact]
    public void Comparisons_arithmetic_and_params()
    {
        var facts = new Dictionary<string, object?> { ["a"] = 5, ["b"] = 2.0, ["kind"] = "Communication" };
        Assert.True(Test("a > b and a + b == 7", facts));
        Assert.True(Test("a / b >= $min", facts, new() { ["min"] = 2.5 }));
        Assert.True(Test("kind == 'communication' and kind in ['Communication', 'TeamGroup']", facts));
        Assert.False(Test("not (a > 1)", facts));
        Assert.True(Test("-a < 0", facts));
    }

    [Fact]
    public void Unknown_flows_through_and_kleene_logic_short_circuits()
    {
        var facts = new Dictionary<string, object?> { ["known"] = 1, ["missing"] = null };
        Assert.Null(Test("missing > 0", facts));
        Assert.False(Test("known > 5 and missing > 0", facts));
        Assert.True(Test("known == 1 or missing > 0", facts));
        Assert.Null(Test("known == 1 and missing > 0", facts));
        Assert.True(Test("missing == null", facts));
        Assert.True(Test("coalesce(missing, 3) == 3", facts));
        Assert.False(Test("known(missing)", facts));
    }

    [Fact]
    public void Bad_expressions_are_reported_with_position()
    {
        var ex = Assert.Throws<RuleException>(() => Expression.Parse("a > > 1"));
        Assert.Contains("position", ex.Message, StringComparison.Ordinal);
        Assert.Throws<RuleException>(() => Expression.Parse("nope(a)"));
        Assert.Throws<RuleException>(() => Expression.Parse("'open"));
    }

    [Fact]
    public void Days_since_uses_the_pinned_clock()
    {
        var now = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        Values.Now = now;
        Assert.True(Test("days_since(when) == 10", new() { ["when"] = now.AddDays(-10) }));
    }
}

public class ThemeTests
{
    [Fact]
    public void Wcag_contrast_matches_known_pairs()
    {
        Assert.Equal(21, Rgb.Black.ContrastWith(Rgb.White), 2);
        Assert.Equal(4.48, Rgb.Parse("#777777").ContrastWith(Rgb.White), 2);
        Assert.Equal(1, Rgb.Parse("#abc").ContrastWith(Rgb.Parse("#aabbcc")), 3);
    }

    [Fact]
    public void Generated_palette_is_close_to_the_fluent_default_for_0078d4()
    {
        var theme = ThemeGenerator.Generate(new ThemeInput("Fluent", Rgb.Parse("#0078d4")));
        static double Distance(string a, string b)
        {
            var x = Rgb.Parse(a);
            var y = Rgb.Parse(b);
            return Math.Abs(x.R - y.R) + Math.Abs(x.G - y.G) + Math.Abs(x.B - y.B);
        }

        // Fluent's published default palette for #0078d4.
        Assert.True(Distance(theme.Palette["themeLighterAlt"], "#eff6fc") < 12);
        Assert.True(Distance(theme.Palette["themeLight"], "#c7e0f4") < 20);
        Assert.True(Distance(theme.Palette["themeDark"], "#005a9e") < 12);
        Assert.True(Distance(theme.Palette["themeDarker"], "#004578") < 12);
        Assert.Equal(22, theme.Palette.Count);
        Assert.True(theme.Accessible);
    }

    [Fact]
    public void Yellow_fails_and_the_suggestion_passes()
    {
        var yellow = Rgb.Parse("#f2c811");
        Assert.False(ThemeGenerator.Generate(new ThemeInput("Y", yellow)).Accessible);
        var better = ThemeGenerator.SuggestAccessiblePrimary(yellow, Rgb.White);
        Assert.NotNull(better);
        Assert.True(ThemeGenerator.Generate(new ThemeInput("Y", better!.Value)).Accessible);
    }

    [Fact]
    public void Theme_json_round_trips_and_powershell_names_both_modules()
    {
        var theme = ThemeGenerator.Generate(new ThemeInput("Contoso", Rgb.Parse("#0f6cbd")));
        var back = ThemeGenerator.FromJson(theme.ToJson());
        Assert.Equal(theme.Palette["themePrimary"], back.Palette["themePrimary"]);
        var ps = theme.ToPowerShell(true);
        Assert.Contains("Add-SPOTheme", ps, StringComparison.Ordinal);
        Assert.Contains("Add-PnPTenantTheme", ps, StringComparison.Ordinal);
        Assert.Contains("Set-SPOHideDefaultThemes", ps, StringComparison.Ordinal);
    }
}

public class SkillTests
{
    internal static SkillLibrary BuiltIn() => SkillLibrary.Load([(Path.Combine(AppContext.BaseDirectory, "skills"), SkillLayer.BuiltIn)]);

    [Fact]
    public void Built_in_skills_load_and_validate_without_errors()
    {
        var library = BuiltIn();
        Assert.Empty(library.Errors);
        Assert.True(library.Skills.Count >= 14);
        var errors = SkillValidator.Validate(library, new DateOnly(2026, 10, 27)).Where(i => i.Level == IssueLevel.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        Assert.All(library.Rules.Where(r => r.Basis == Basis.Documented), r => Assert.False(string.IsNullOrWhiteSpace(r.Quote)));
    }

    [Fact]
    public void Readme_and_roadmap_counts_match_the_skill_library()
    {
        var library = BuiltIn();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hubwright.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var rules = library.Rules.Count;
        var skills = library.Skills.Count;
        Assert.Contains($"{rules} rules in {skills} skills", File.ReadAllText(Path.Combine(dir.FullName, "README.md")), StringComparison.Ordinal);
        Assert.Contains($"{skills} skills and {rules} rules", File.ReadAllText(Path.Combine(dir.FullName, "docs", "roadmap.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_scaffolded_skill_validates_and_a_later_layer_replaces_by_id()
    {
        var root = Directory.CreateTempSubdirectory("hubwright-skills").FullName;
        SkillScaffold.Create(root, "hub-sites", "My hubs", new DateOnly(2026, 10, 27));
        var library = SkillLibrary.Load([(Path.Combine(AppContext.BaseDirectory, "skills"), SkillLayer.BuiltIn), (root, SkillLayer.User)]);
        var skill = library.Find("hub-sites")!;
        Assert.Equal(SkillLayer.User, skill.Layer);
        Assert.Contains(library.Overrides, o => o.Id == "hub-sites" && o.Outdated);
        Assert.DoesNotContain(SkillValidator.Validate(skill, new DateOnly(2026, 10, 27)), i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Documented_rule_without_quote_and_unknown_fact_are_errors()
    {
        var root = Directory.CreateTempSubdirectory("hubwright-bad").FullName;
        var dir = SkillScaffold.Create(root, "bad-skill", "Bad", new DateOnly(2026, 10, 27));
        File.WriteAllText(Path.Combine(dir, "rules.yaml"), """
            rules:
              - id: bad.one
                title: Bad
                scope: site
                when: notAFact > 1
                basis: documented
            """);
        var issues = SkillValidator.Validate(SkillLoader.Load(dir, SkillLayer.User), new DateOnly(2026, 10, 27));
        Assert.Contains(issues, i => i.Message.Contains("no fact 'notAFact'", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("exact quote", StringComparison.Ordinal));
    }

    [Fact]
    public void Drift_normalizes_inline_markup_and_quote_styles()
    {
        Assert.True(SourceDrift.Contains("<p>Global navigation is enabled with the <a href=\"x\">SharePoint app bar</a> on your <a>home site</a>.</p>", "Global navigation is enabled with the SharePoint app bar on your home site."));
        Assert.True(SourceDrift.Contains("subsites aren’t recommended – really", "subsites aren't recommended - really"));
        Assert.False(SourceDrift.Contains("<p>something else</p>", "subsites"));
    }

    [Fact]
    public async Task Drift_reports_missing_quotes_and_unreachable_pages()
    {
        var hub = BuiltIn().Find("hub-sites")!;
        var results = await SourceDrift.CheckAsync([hub], new FakeFetcher(null), CancellationToken.None);
        Assert.All(results, r => Assert.Equal(DriftStatus.Unreachable, r.Status));
        results = await SourceDrift.CheckAsync([hub], new FakeFetcher("<html>nothing here</html>"), CancellationToken.None);
        Assert.All(results, r => Assert.Equal(DriftStatus.QuoteMissing, r.Status));
    }

    private sealed class FakeFetcher(string? text) : ISourceFetcher
    {
        public Task<(string? Text, string? Error)> FetchAsync(Uri url, CancellationToken cancellationToken)
            => Task.FromResult<(string?, string?)>((text, text is null ? "offline" : null));
    }
}

public class AssessmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static Assessment Run(TenantSnapshot snapshot, IReadOnlyDictionary<string, RuleOverride>? overrides = null)
    {
        Values.Now = Now;
        var library = SkillTests.BuiltIn();
        return Assessment.Run(snapshot, library.Rules, library.Versions, overrides);
    }

    [Fact]
    public void Messy_tenant_raises_the_expected_findings()
    {
        var a = Run(SampleTenants.Fabrikam(Now));
        var ids = a.Findings.Select(f => f.RuleId).ToHashSet();
        foreach (var expected in new[] { "ia.subsites", "hubs.depth", "hubs.nav-sites", "hubs.extranet", "home.missing", "sharing.default-link", "sharing.anyone-expiry", "perm.ownerless", "perm.unique-scopes", "lifecycle.all-three", "lifecycle.inactive-site", "brand.contrast", "modern.classic-theme", "nav.default-links", "search.answers" })
        {
            Assert.Contains(expected, ids);
        }

        Assert.Empty(a.Errors);
        Assert.True(a.Overall < 60);
    }

    [Fact]
    public void Healthy_tenant_scores_high()
    {
        var a = Run(SampleTenants.Northwind(Now));
        Assert.Empty(a.Errors);
        Assert.True(a.Overall >= 90, $"overall {a.Overall}");
        Assert.DoesNotContain(a.Findings, f => f.Severity >= Severity.High);
    }

    [Fact]
    public void Missing_data_is_not_assessed_rather_than_passed()
    {
        var snapshot = new TenantSnapshot { Tenant = "Empty", Sites = [new SiteInfo { Url = "https://x.sharepoint.com/sites/a", Title = "A" }] };
        var a = Run(snapshot);
        var owner = a.Results.Single(r => r.Rule.Id == "perm.ownerless");
        Assert.Equal(RuleStatus.NotAssessed, owner.Status);
        Assert.DoesNotContain(a.Findings, f => f.RuleId == "perm.ownerless");
    }

    [Fact]
    public void Overrides_disable_rules_and_change_thresholds()
    {
        var snapshot = SampleTenants.Fabrikam(Now);
        var a = Run(snapshot, new Dictionary<string, RuleOverride>
        {
            ["home.missing"] = new() { Disabled = true },
            ["lifecycle.inactive-site"] = new() { Params = new() { ["inactiveDays"] = 10_000.0 } },
        });
        Assert.DoesNotContain(a.Findings, f => f.RuleId is "home.missing" or "lifecycle.inactive-site");
    }

    [Fact]
    public void Blueprint_passes_its_own_rules_and_keeps_extranets_out_of_hubs()
    {
        var blueprint = BlueprintGenerator.Generate(new IntranetAnswers { Organization = "Contoso", Employees = 2000, Regions = ["A", "B"], ExternalCollaboration = true, Goals = ["news", "collaboration", "onboarding"], BrandColor = "#f2c811" });
        var snapshot = blueprint.ToSnapshot();
        Assert.All(snapshot.Sites.Where(s => s.IsExtranet), s => Assert.Null(s.HubId));
        Assert.All(snapshot.Hubs, h => Assert.True(Navigation.Count(h.Navigation) <= 100));
        Assert.NotEqual("#f2c811", blueprint.BrandColor); // made accessible
        var a = Run(snapshot);
        Assert.Empty(a.Errors);
        Assert.True(a.Overall >= 95, $"overall {a.Overall}: {string.Join(", ", a.Findings.Select(f => f.RuleId))}");
        Assert.Equal(blueprint.ToJson(), Blueprint.FromJson(blueprint.ToJson()).ToJson());
    }

    [Fact]
    public void Reports_carry_sources_and_mockup_loads_nothing_remote()
    {
        var a = Run(SampleTenants.Fabrikam(Now));
        var md = Reports.ReportWriter.Markdown(a);
        Assert.Contains("learn.microsoft.com", md, StringComparison.Ordinal);
        Assert.Contains("heuristic", md, StringComparison.Ordinal);
        System.Text.Json.JsonDocument.Parse(Reports.ReportWriter.Json(a)).Dispose();
        var html = Preview.MockupRenderer.Render(Preview.MockupModel.FromBlueprint(BlueprintGenerator.Generate(new IntranetAnswers())));
        Assert.Contains("--themePrimary", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script src", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<link ", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_shows_resolved_findings()
    {
        var before = Run(SampleTenants.Fabrikam(Now));
        var fixedSnapshot = SampleTenants.Fabrikam(Now);
        fixedSnapshot.Settings.DefaultLinkType = DefaultLinkType.Organization;
        var after = Run(fixedSnapshot);
        var diff = AssessmentDiff.Between(before, after);
        Assert.Contains(diff.ResolvedFindings, f => f.StartsWith("sharing.default-link", StringComparison.Ordinal));
    }
}
