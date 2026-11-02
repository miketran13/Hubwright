using System.Net;
using System.Text;
using Hubwright.Agents;
using Hubwright.Collectors;
using Hubwright.Engine;
using Hubwright.Http;
using Hubwright.Implementation;
using Hubwright.Proposals;
using Hubwright.Reports;
using Hubwright.Rules;
using Hubwright.Snapshot;
using Hubwright.Storage;

namespace Hubwright.Tests;

/// <summary>Sites the person who scans cannot open: listed from the usage report, never assumed fine.</summary>
public class UnreadSiteTests
{
    private const string Root = "https://contoso.sharepoint.com";
    private const string HubId = "11111111-2222-3333-4444-555555555555";

    private static async Task<TenantSnapshot> ScanAsync(FakeTenant tenant)
    {
        var http = new TenantHttp((_, _) => Task.FromResult("token"), new HttpClient(tenant));
        return await new TenantCollector(http, new TenantConnection { Name = "contoso" }, new ScanOptions()).CollectAsync(null, null, CancellationToken.None);
    }

    [Fact]
    public async Task Sites_search_hides_come_from_the_usage_report_and_unreadable_ones_are_marked()
    {
        var snapshot = await ScanAsync(new FakeTenant());

        Assert.Equal(
            [$"{Root}/sites/intranet", $"{Root}/sites/open", $"{Root}/sites/private"],
            snapshot.Sites.Select(s => s.Url).Order(StringComparer.Ordinal));
        Assert.False(snapshot.SiteListIncomplete);

        var visible = snapshot.Sites.Single(s => s.Url.EndsWith("/intranet", StringComparison.Ordinal));
        Assert.True(visible.Readable);

        // Not in search, but readable: details and hub are read like any other site.
        var open = snapshot.Sites.Single(s => s.Url.EndsWith("/open", StringComparison.Ordinal));
        Assert.True(open.Readable);
        Assert.Equal("Open projects", open.Title);
        Assert.Equal(HubId, open.HubId);
        Assert.False(snapshot.Unread(ScanAreas.Hubs, open.Url));

        // Not a member: known from the report only, everything else unknown.
        var hidden = snapshot.Sites.Single(s => s.Url.EndsWith("/private", StringComparison.Ordinal));
        Assert.False(hidden.Readable);
        Assert.Equal(SiteKind.TeamGroup, hidden.Kind);
        Assert.NotNull(hidden.LastActivity);
        Assert.Null(hidden.OwnerCount);
        Assert.Null(hidden.Permissions.EeeuGrants);
        Assert.Contains(snapshot.ScanIssues, i => i.Area == ScanAreas.SiteDetail && i.Target == hidden.Url && i.Reason.StartsWith("Access denied", StringComparison.Ordinal));
        Assert.True(snapshot.Unread(ScanAreas.Hubs, hidden.Url));
        Assert.Single(snapshot.ScanIssues, i => i.Target == hidden.Url && i.Area == ScanAreas.SiteDetail);

        var empty = new Dictionary<string, object?>();
        Assert.Same(Expression.Unknown, new SnapshotFacts(RuleScope.Site, new FactContext(snapshot, Site: hidden), empty).Get("inHub"));
        Assert.Equal(false, new SnapshotFacts(RuleScope.Site, new FactContext(snapshot, Site: visible), empty).Get("inHub"));
        Assert.Equal(1, new SnapshotFacts(RuleScope.Tenant, new FactContext(snapshot), empty).Get("unreadableSites"));
        Assert.Equal(3, new SnapshotFacts(RuleScope.Tenant, new FactContext(snapshot), empty).Get("siteCount"));

        // Personal and deleted sites in the report are not added.
        Assert.DoesNotContain(snapshot.Sites, s => s.Url.Contains("-my.", StringComparison.Ordinal) || s.Url.EndsWith("/gone", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_the_usage_report_site_counts_are_not_assessed(bool concealed)
    {
        var snapshot = await ScanAsync(new FakeTenant { ReportDenied = !concealed, ReportConcealed = concealed });

        Assert.True(snapshot.SiteListIncomplete);
        Assert.Contains(snapshot.ScanIssues, i => i.Area == ScanAreas.Sites && i.Target is null && i.Reason.StartsWith("Only sites you can access", StringComparison.Ordinal));
        var facts = new SnapshotFacts(RuleScope.Tenant, new FactContext(snapshot), new Dictionary<string, object?>());
        Assert.Same(Expression.Unknown, facts.Get("siteCount"));
        Assert.Same(Expression.Unknown, facts.Get("sitesWithoutHub"));
    }

    [Fact]
    public void The_proposal_and_the_build_plan_send_someone_with_access_to_unread_sites()
    {
        var snapshot = SampleTenants.Fabrikam();
        var site = snapshot.Sites.First(s => s.Kind == SiteKind.TeamGroup && !s.IsHub);
        site.Readable = false;
        site.OwnerCount = null;
        site.HubId = null;
        snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.Hubs, "Hub association not read.") { Target = site.Url });

        var store = new TenantStore("fabrikam", Directory.CreateTempSubdirectory("hubwright-unread").FullName);
        store.SaveSnapshot(snapshot);
        var skills = SkillTests.BuiltIn();
        var current = new Workspace(store, skills).RequireAssessment();
        var proposal = IntranetProposalGenerator.Generate(current.Snapshot, current, IntranetProposalGenerator.Seed(current.Snapshot).Answers, skills.Templates, []);

        Assert.Equal(1, proposal.UnreadSites);
        var plan = proposal.Sites.Single(s => s.Url == site.Url);
        Assert.Contains(SiteAction.CheckUnread, plan.Actions);
        Assert.DoesNotContain(SiteAction.AddOwners, plan.Actions);
        Assert.Contains(plan.Reasons, r => r.StartsWith("Not readable", StringComparison.Ordinal));
        var item = proposal.Phases.SelectMany(p => p.Items).Single(i => i.Text.StartsWith("Check 1 site(s) the scan could not read", StringComparison.Ordinal));
        Assert.Equal("unread-site", item.Hint);

        var build = ImplementationPlanner.Build(proposal, current.Snapshot, skills.Templates);
        var step = build.Find("unread")!;
        Assert.Equal(StepKind.Guided, step.Kind);
        Assert.Equal("unread-site", step.Hint);
        Assert.Contains(step.Instructions, i => i.Contains(site.Url, StringComparison.Ordinal));
        Assert.NotNull(skills.HintBook().Find("unread-site"));
        Assert.Contains("could not be read with your access", ProposalWriter.Markdown(proposal), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_report_warns_about_the_sites_you_cannot_open_and_says_how_to_get_access()
    {
        var skills = SkillTests.BuiltIn();
        var store = new TenantStore("fabrikam", Directory.CreateTempSubdirectory("hubwright-access").FullName);
        var snapshot = SampleTenants.Fabrikam();
        store.SaveSnapshot(snapshot);
        var clean = new Workspace(store, skills).RequireAssessment();
        Assert.Empty(SiteAccess.Unread(clean.Snapshot));
        Assert.DoesNotContain("you lack permissions on", ReportWriter.Markdown(clean), StringComparison.Ordinal);
        Assert.DoesNotContain("you lack permissions on", ReportWriter.Html(clean), StringComparison.Ordinal);

        var hidden = snapshot.Sites.First(s => s.Kind == SiteKind.TeamGroup && !s.IsHub);
        hidden.Readable = false;
        snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.SiteDetail, "Access denied (403): you are not a member.") { Target = hidden.Url });

        // Readable, but its permissions answered 403 (a member, not an owner); another permissions gap that isn't a denial is not listed.
        var member = snapshot.Sites.First(s => s.Readable != false && !s.IsHub && s.Url != hidden.Url);
        snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.Permissions, "Access denied: 403 Forbidden from roleassignments") { Target = member.Url });
        snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.Permissions, "Not read in detail (scan.maxDetailedSites = 200).") { Target = snapshot.Sites.Last().Url });
        store.SaveSnapshot(snapshot);
        var a = new Workspace(store, skills).RequireAssessment();

        var sites = SiteAccess.Unread(a.Snapshot);
        Assert.Equal(2, sites.Count);
        var unread = sites[0];
        Assert.Equal(hidden.Url, unread.Url);
        Assert.Equal(SiteAccessLevel.CannotOpen, unread.Level);
        Assert.StartsWith("Access denied", unread.Reason, StringComparison.Ordinal);
        Assert.Equal(SiteAccessLevel.CannotReadPermissions, sites[1].Level);
        Assert.Equal(member.Url, sites[1].Url);

        var markdown = ReportWriter.Markdown(a);
        Assert.Contains("## ⚠ 2 sites you lack permissions on, not fully assessed (1 you can't open, 1 whose permissions you can't read)", markdown, StringComparison.Ordinal);
        Assert.True(markdown.IndexOf("you lack permissions on", StringComparison.Ordinal) < markdown.IndexOf("## Scores", StringComparison.Ordinal));
        Assert.Contains(member.Url, markdown, StringComparison.Ordinal);
        Assert.Contains(hidden.Url, markdown, StringComparison.Ordinal);
        Assert.Contains(SiteAccess.ActiveSitesLink, markdown, StringComparison.Ordinal);
        Assert.Contains(SiteAccess.SiteAdminQuote, markdown, StringComparison.Ordinal);

        var html = ReportWriter.Html(a);
        Assert.Contains("2 sites you lack permissions on", html, StringComparison.Ordinal);
        Assert.Contains($"href=\"{hidden.Url}\"", html, StringComparison.Ordinal);
        Assert.Contains("\"unreadSites\"", ReportWriter.Json(a), StringComparison.Ordinal);
        Assert.Contains(hidden.Url, SiteAccess.Csv(SiteAccess.Unread(a.Snapshot)), StringComparison.Ordinal);

        // The hint behind it quotes Microsoft on what a site admin can see.
        var hint = skills.HintBook().Find("unread-site")!;
        Assert.Equal(SiteAccess.SiteAdminQuote, hint.Quote);
    }

    [Fact]
    public void Hubs_whose_site_the_scan_could_not_see_are_not_kept_and_do_not_break_the_plan()
    {
        var snapshot = SampleTenants.Fabrikam();
        var store = new TenantStore("fabrikam", Directory.CreateTempSubdirectory("hubwright-hubless").FullName);
        var skills = SkillTests.BuiltIn();
        var answers = IntranetProposalGenerator.Seed(snapshot).Answers;

        // SP.HubSites lists these hubs without a SiteUrl (the plan has an HR and a Finance hub).
        snapshot.Hubs.InsertRange(0, new[] { "HR", "Finance" }.Select(t => new HubInfo { Id = Guid.NewGuid().ToString(), Title = t }));
        snapshot.ScanIssues.Add(new ScanIssue(ScanAreas.Navigation, "The hub came back without a site URL.") { Target = "" });
        store.SaveSnapshot(snapshot);
        var current = new Workspace(store, skills).RequireAssessment();
        var proposal = IntranetProposalGenerator.Generate(current.Snapshot, current, answers, skills.Templates, [], patterns: skills.Patterns);

        Assert.DoesNotContain(proposal.Blueprint.Hubs, h => h.ExistingUrl is { Length: 0 });
        Assert.Equal(2, proposal.Blueprint.Rationale.Count(r => r.Contains("the scan could not see its site", StringComparison.Ordinal)));
        Assert.DoesNotContain(proposal.Designs, d => d.Url.Length == 0);
        _ = ImplementationPlanner.Build(proposal, current.Snapshot, skills.Templates);
        _ = SitePrototype.Render(proposal, skills.Templates);
        _ = StructurePreview.Render(proposal, current.Snapshot);
    }

    /// <summary>
    /// A tenant where search returns /sites/intranet only; the usage report also lists /sites/open
    /// (readable, in a hub), /sites/private (access denied), a OneDrive and a deleted site.
    /// </summary>
    private sealed class FakeTenant : HttpMessageHandler
    {
        public bool ReportDenied { get; init; }

        public bool ReportConcealed { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            static HttpResponseMessage Text(string text, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

            if (request.Method != HttpMethod.Get && !path.Contains("/_api/thememanager/Get", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"The scan sent {request.Method} {uri}.");
            }

            if (uri.Host == "graph.microsoft.com")
            {
                if (ReportDenied)
                {
                    return Task.FromResult(Text("{}", HttpStatusCode.Forbidden));
                }

                const string header = "Report Refresh Date,Site Id,Site URL,Owner Display Name,Is Deleted,Last Activity Date,File Count,Active File Count,Page View Count,Visited Page Count,Storage Used (Byte),Storage Allocated (Byte),Root Web Template,Owner Principal Name,Report Period";
                string Row(string url, string deleted, string template) => $"2026-10-28,id,{(ReportConcealed ? "3F2A9C" : url)},Owner,{deleted},2026-10-01,10,1,5,2,1048576,0,{template},owner@contoso.com,180";
                return Task.FromResult(Text(string.Join('\n',
                    header,
                    Row($"{Root}/sites/intranet", "False", "SITEPAGEPUBLISHING#0"),
                    Row($"{Root}/sites/open", "False", "STS#3"),
                    Row($"{Root}/sites/private", "False", "GROUP#0"),
                    Row("https://contoso-my.sharepoint.com/personal/someone", "False", "SPSPERS#10"),
                    Row($"{Root}/sites/gone", "True", "STS#3"))));
            }

            if (path.EndsWith("/_api/search/query", StringComparison.Ordinal))
            {
                return Task.FromResult(Text("""{"PrimaryQueryResult":{"RelevantResults":{"Table":{"Rows":[{"Cells":[{"Key":"Path","Value":"ROOT/sites/intranet"},{"Key":"Title","Value":"Intranet"},{"Key":"WebTemplate","Value":"SITEPAGEPUBLISHING"}]}]}}}}""".Replace("ROOT", Root, StringComparison.Ordinal)));
            }

            if (path.StartsWith("/sites/private", StringComparison.Ordinal))
            {
                return Task.FromResult(Text("""{"odata.error":{"message":{"value":"Access denied."}}}""", HttpStatusCode.Forbidden));
            }

            if (path.EndsWith("/_api/web", StringComparison.Ordinal))
            {
                var title = path.StartsWith("/sites/open", StringComparison.Ordinal) ? "Open projects" : "Intranet";
                return Task.FromResult(Text($$"""{"Title":"{{title}}","WebTemplate":"SITEPAGEPUBLISHING","Configuration":0}"""));
            }

            if (path.EndsWith("/_api/site", StringComparison.Ordinal))
            {
                return Task.FromResult(Text($$"""{"HubSiteId":"{{HubId}}"}"""));
            }

            return Task.FromResult(Text(path.Contains("SPO.Tenant", StringComparison.Ordinal) || path.Contains("thememanager", StringComparison.Ordinal) || path.Contains("SPHSite", StringComparison.Ordinal) || path.Contains("MenuState", StringComparison.Ordinal) ? "{}" : """{"value":[]}"""));
        }
    }
}
