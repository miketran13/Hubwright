using Hubwright.Agents;
using Hubwright.Engine;
using Hubwright.Http;
using Hubwright.Implementation;
using Hubwright.Proposals;
using Hubwright.Storage;
using Hubwright.Tools;

namespace Hubwright.Tests;

public class PnpTests
{
    private static ImplementationPlan Plan()
    {
        var store = new TenantStore("fabrikam", Directory.CreateTempSubdirectory("hubwright-pnp").FullName);
        store.SaveSnapshot(SampleTenants.Fabrikam());
        var skills = SkillTests.BuiltIn();
        var current = new Workspace(store, skills).RequireAssessment();
        var seed = IntranetProposalGenerator.Seed(current.Snapshot);
        var proposal = IntranetProposalGenerator.Generate(current.Snapshot, current, seed.Answers, skills.Templates, []);
        return ImplementationPlanner.Build(proposal, current.Snapshot, skills.Templates);
    }

    [Fact]
    public void Launch_steps_add_org_news_and_unpublished_drafts_and_every_PnP_script_passes_the_shell_guards()
    {
        var plan = Plan();
        var news = plan.Steps.Where(s => s.Operation == StepOperation.AddOrgNewsSite).ToList();
        Assert.NotEmpty(news);
        Assert.All(news, s => Assert.Contains("-admin.sharepoint.com", PnpScripts.ConnectUrl(plan, s), StringComparison.Ordinal));
        var welcome = plan.Find("welcome-home")!;
        Assert.Equal(StepOperation.AddStarterPage, welcome.Operation);
        var script = PnpScripts.Script(plan, welcome);
        Assert.DoesNotContain("-Publish", script, StringComparison.Ordinal);
        Assert.Contains("Get-PnPPage", script, StringComparison.Ordinal);
        Assert.Contains(ImplementationPlanner.SampleMarker, script, StringComparison.Ordinal);
        Assert.Contains(plan.Steps, s => s.Id == "brand-center" && s.Kind == StepKind.Guided);
        Assert.Contains(plan.Steps, s => s.Id == "teams-dashboard" && s.DependsOn.Contains("set-home-site"));

        foreach (var step in plan.Steps.Where(s => PnpScripts.IsPnp(s.Operation)))
        {
            var text = PnpScripts.Script(plan, step);
            PnpShell.CheckCmdlets(text);
            PnpShell.CheckTenantParameters(text);
        }

        Assert.Throws<InvalidOperationException>(() => PnpShell.CheckCmdlets("Remove-PnPPage -Identity 'Welcome'"));
    }

    [Fact]
    public void Home_site_navigation_templates_header_and_logo_are_automatic_PnP_steps_with_a_by_hand_route()
    {
        var plan = Plan();
        Assert.Contains(plan.Steps, s => s.Operation == StepOperation.SetHomeSite && s.Kind == StepKind.Automatic);
        Assert.Contains(plan.Steps, s => s.Operation == StepOperation.BuildNavigation && s.Id == "home-nav");
        Assert.Contains(plan.Steps, s => s.Operation == StepOperation.BuildNavigation && s.Id.StartsWith("nav-", StringComparison.Ordinal));
        Assert.Contains(plan.Steps, s => s.Operation == StepOperation.ApplySiteTemplate && s.Parameters["template"] == "HumanResources");
        Assert.Contains(plan.Steps, s => s.Operation == StepOperation.SetSiteHeader && s.Parameters["emphasis"] == "Strong");
        Assert.Contains(plan.Steps, s => s.Id == "m365-header" && s.Kind == StepKind.Guided);
        Assert.All(plan.Steps.Where(s => PnpScripts.IsPnp(s.Operation)), s =>
        {
            Assert.NotNull(ImplementationPlanner.Fallback(plan, s));
            Assert.NotNull(s.Source);
        });
        Assert.DoesNotContain(plan.Steps, s => s.Id.StartsWith("template-", StringComparison.Ordinal));
    }

    [Fact]
    public void Scripts_quote_every_value_use_only_their_cmdlets_and_never_hold_the_token()
    {
        var plan = Plan();
        var step = plan.Steps.First(s => s.Operation == StepOperation.BuildNavigation && s.Id.StartsWith("nav-", StringComparison.Ordinal));
        step.Parameters["nav"] = """[{"title":"Partners' area","url":"https://contoso.sharepoint.com/sites/p'; Remove-PnPSite -Force; '","children":[]}]""";
        var script = PnpScripts.Script(plan, step);
        Assert.Contains("'Partners'' area'", script, StringComparison.Ordinal);
        Assert.Contains("'https://contoso.sharepoint.com/sites/p''; Remove-PnPSite -Force; '''", script, StringComparison.Ordinal);
        Assert.Contains("-AccessToken $env:HUBWRIGHT_TOKEN", script, StringComparison.Ordinal);
        foreach (var s in plan.Steps.Where(s => PnpScripts.IsPnp(s.Operation) && s != step))
        {
            var text = PnpScripts.Script(plan, s);
            Assert.DoesNotContain("eyJ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Remove-", text.Replace("Remove-Item $package", "", StringComparison.Ordinal), StringComparison.Ordinal);
        }

        Assert.Throws<InvalidOperationException>(() => PnpScripts.Identifier("Department; Remove-PnPSite"));
    }

    [Fact]
    public async Task The_runner_passes_the_token_and_logo_only_through_the_environment()
    {
        var plan = Plan();
        var folder = Directory.CreateTempSubdirectory("hubwright-assets").FullName;
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "assets", "logo.png"), [137, 80, 78, 71]);
        var logo = plan.Steps.First(s => s.Operation == StepOperation.SetSiteLogo);
        logo.Parameters["logo"] = "assets/logo.png";
        var header = plan.Steps.First(s => s.Operation == StepOperation.SetSiteHeader);
        foreach (var dependency in logo.DependsOn.Concat(header.DependsOn))
        {
            plan.Find(dependency)!.Status = StepStatus.Done;
        }

        var shell = new FakePnp();
        var audit = new AuditLog(Path.Combine(folder, "audit.jsonl"));
        var client = new HttpClient(new HttpClientHandler());
        var runner = new ImplementationRunner(plan, () => { }, new TenantHttp((_, _) => Task.FromResult("t"), client), new TenantWriter((_, _) => Task.FromResult("t"), client), audit)
        {
            Pnp = shell,
            Tokens = (resource, _) => Task.FromResult("token-for-" + resource),
            AssetsFolder = folder,
        };
        await runner.RunAsync([logo.Id, header.Id], null, CancellationToken.None);

        Assert.Equal(StepStatus.Done, logo.Status);
        Assert.Equal(StepStatus.Done, header.Status);
        Assert.All(shell.Runs, r => Assert.DoesNotContain("token-for-", r.Script, StringComparison.Ordinal));
        Assert.All(shell.Runs, r => Assert.Equal("token-for-SharePoint", r.Environment[PnpScripts.TokenVariable]));
        Assert.Equal(Path.Combine(folder, "assets", "logo.png"), shell.Runs.Single(r => r.Script.Contains("Add-PnPFile", StringComparison.Ordinal)).Environment[PnpScripts.LogoVariable]);
        Assert.DoesNotContain(audit.Read(), e => e.Detail.Contains("token-for-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_PnP_or_a_logo_the_steps_say_what_to_do()
    {
        var plan = Plan();
        var logo = plan.Steps.First(s => s.Operation == StepOperation.SetSiteLogo);
        var nav = plan.Steps.First(s => s.Id == "home-nav");
        foreach (var dependency in logo.DependsOn.Concat(nav.DependsOn))
        {
            plan.Find(dependency)!.Status = StepStatus.Done;
        }

        var client = new HttpClient(new HttpClientHandler());
        var audit = new AuditLog(Path.Combine(Directory.CreateTempSubdirectory("hubwright-audit").FullName, "audit.jsonl"));
        var withShell = new ImplementationRunner(plan, () => { }, new TenantHttp((_, _) => Task.FromResult("t"), client), new TenantWriter((_, _) => Task.FromResult("t"), client), audit) { Pnp = new FakePnp(), Tokens = (_, _) => Task.FromResult("t") };
        await withShell.RunAsync([logo.Id], null, CancellationToken.None);
        Assert.Equal(StepStatus.Skipped, logo.Status);
        Assert.Contains("No logo", logo.Result, StringComparison.Ordinal);

        var noShell = new ImplementationRunner(plan, () => { }, new TenantHttp((_, _) => Task.FromResult("t"), client), new TenantWriter((_, _) => Task.FromResult("t"), client), audit);
        await noShell.RunAsync([nav.Id], null, CancellationToken.None);
        Assert.Equal(StepStatus.Failed, nav.Status);
        Assert.Contains("PnP PowerShell", nav.Result, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Remove-PnPSite -Identity https://contoso.sharepoint.com/sites/hr -Force")]
    [InlineData("Set-PnPTenant -SharingCapability ExternalUserAndGuestSharing")]
    [InlineData("Invoke-Expression 'calc'")]
    public async Task The_shell_refuses_any_cmdlet_outside_the_list_before_starting_PowerShell(string script)
        => await Assert.ThrowsAsync<InvalidOperationException>(() => new PnpShell().RunAsync(script, new Dictionary<string, string>(), CancellationToken.None));

    [Fact]
    public void Scripts_report_why_they_stopped_and_read_a_labels_children_by_id()
    {
        var plan = Plan();
        var nav = PnpScripts.Script(plan, plan.Steps.First(s => s.Id == "home-nav"));
        Assert.Contains("(Get-PnPNavigationNode -Id $parent.Id).Children", nav, StringComparison.Ordinal);
        Assert.DoesNotContain("@($parent.Children)", nav, StringComparison.Ordinal);
        Assert.All(plan.Steps.Where(s => PnpScripts.IsPnp(s.Operation)), s => Assert.Contains("trap {", PnpScripts.Script(plan, s), StringComparison.Ordinal));

        var run = new PnpRun(1, "", "Set-PnPWebHeader: C:\\x.ps1:4\nLine |\n   4 |  Set-PnPWebHeader\n" + PnpScripts.ErrorMarker + " Access denied.\n");
        Assert.Equal("Access denied.", run.Failure);
    }

    [Fact]
    public async Task The_setup_check_reads_the_installed_versions_as_plain_numbers()
    {
        var pwsh = PnpShell.FindPowerShell();
        if (pwsh is null)
        {
            return;
        }

        var status = await PnpShell.CheckAsync();
        Assert.Matches(@"^\d+\.\d+", status.PowerShellVersion);
        Assert.True(status.PnpVersion is null || System.Text.RegularExpressions.Regex.IsMatch(status.PnpVersion, @"^\d+\.\d+(\.\d+)*$"), status.Detail);
    }

    [Fact]
    public void The_look_book_download_link_is_the_raw_file()
        => Assert.Equal(
            "https://raw.githubusercontent.com/SharePoint/sp-dev-provisioning-templates/master/tenant/thelanding/thelanding.pnp",
            ImplementationPlanner.RawPackage("https://github.com/SharePoint/sp-dev-provisioning-templates/blob/master/tenant/thelanding/thelanding.pnp"));

    private sealed class FakePnp : IPnpShell
    {
        public List<(string Script, IReadOnlyDictionary<string, string> Environment)> Runs { get; } = [];

        public Task<PnpRun> RunAsync(string script, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
        {
            Runs.Add((script, new Dictionary<string, string>(environment)));
            return Task.FromResult(new PnpRun(0, PnpScripts.ResultMarker + " ok\n", ""));
        }
    }
}
