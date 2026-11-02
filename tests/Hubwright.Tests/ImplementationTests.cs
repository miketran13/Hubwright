using System.Net;
using System.Text;
using Hubwright.Agents;
using Hubwright.Engine;
using Hubwright.Http;
using Hubwright.Implementation;
using Hubwright.Proposals;
using Hubwright.Snapshot;
using Hubwright.Storage;

namespace Hubwright.Tests;

public class ImplementationTests
{
    private const string HubId = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData("POST", "https://contoso.sharepoint.com/_api/SPSiteManager/create", true)]
    [InlineData("POST", "https://contoso.sharepoint.com/sites/hr/_api/site/RegisterHubSite", true)]
    [InlineData("POST", "https://contoso.sharepoint.com/sites/hr/_api/site/JoinHubSite('11111111-2222-3333-4444-555555555555')", true)]
    [InlineData("POST", "https://contoso.sharepoint.com/_api/HubSites/GetById?hubSiteId='11111111-2222-3333-4444-555555555555'", true)]
    [InlineData("POST", "https://contoso.sharepoint.com/_api/thememanager/AddTenantTheme", true)]
    [InlineData("PATCH", "https://graph.microsoft.com/v1.0/admin/sharepoint/settings", true)]
    [InlineData("POST", "https://contoso.sharepoint.com/sites/hr/_api/site/JoinHubSite('{register-hr.hubSiteId}')", false)]
    [InlineData("POST", "https://contoso.sharepoint.com/_api/thememanager/DeleteTenantTheme", false)]
    [InlineData("DELETE", "https://contoso.sharepoint.com/sites/hr", false)]
    [InlineData("POST", "https://contoso.sharepoint.com/_api/web/lists", false)]
    [InlineData("POST", "https://contoso.sharepoint.com/_api/HubSites/GetById", false)]
    [InlineData("PATCH", "https://graph.microsoft.com/v1.0/users/x", false)]
    [InlineData("POST", "https://graph.microsoft.com/v1.0/admin/sharepoint/settings", false)]
    [InlineData("POST", "https://evil.example.com/_api/SPSiteManager/create", false)]
    [InlineData("POST", "http://contoso.sharepoint.com/_api/SPSiteManager/create", false)]
    public void The_writer_only_sends_documented_allow_listed_writes(string method, string url, bool allowed)
        => Assert.Equal(allowed, TenantWriter.IsAllowed(method, new Uri(url)));

    [Fact]
    public async Task A_refused_write_never_reaches_the_network()
    {
        var handler = new FakeSharePoint();
        var writer = new TenantWriter((_, _) => Task.FromResult("token"), new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SendAsync("DELETE", "https://contoso.sharepoint.com/sites/hr", null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    private static (ImplementationPlan Plan, IntranetProposal Proposal) FabrikamPlan()
    {
        var home = Directory.CreateTempSubdirectory("hubwright-impl").FullName;
        var store = new TenantStore("fabrikam", home);
        store.SaveSnapshot(SampleTenants.Fabrikam());
        var skills = SkillTests.BuiltIn();
        var current = new Workspace(store, skills).RequireAssessment();
        var seed = IntranetProposalGenerator.Seed(current.Snapshot);
        var proposal = IntranetProposalGenerator.Generate(current.Snapshot, current, seed.Answers, skills.Templates, []);
        return (ImplementationPlanner.Build(proposal, current.Snapshot, skills.Templates), proposal);
    }

    [Fact]
    public void The_plan_never_automates_anything_hard_to_undo_and_orders_dependencies()
    {
        var (plan, proposal) = FabrikamPlan();
        Assert.DoesNotContain(plan.Steps, s => s.Kind == StepKind.Automatic && s.Risk == StepRisk.Caution);
        Assert.Contains(plan.Steps, s => s.Id == "archive" && s.Kind == StepKind.Guided);
        Assert.Equal(plan.Steps.Count, plan.Steps.Select(s => s.Id).Distinct().Count());
        var position = plan.Steps.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        Assert.All(plan.Steps, s => Assert.All(s.DependsOn, d => Assert.True(position.TryGetValue(d, out var p) && p < position[s.Id], $"{s.Id} depends on {d}")));

        // Every new site is created (or provisioned from its package), and the extranet leaves its hub.
        Assert.All(proposal.NewSites, n => Assert.Contains(plan.Steps, s => s.Target == n.Url && (s.Operation is StepOperation.CreateSite or StepOperation.ProvisionPackage || s.Kind == StepKind.Guided)));
        Assert.Contains(plan.Steps, s => s.Operation == StepOperation.DisassociateFromHub && s.Target!.EndsWith("/sites/partners", StringComparison.Ordinal));
        Assert.Equal("externalUserSharingOnly", plan.Find("sharing")!.Parameters["sharingCapability"]);
    }

    [Fact]
    public void Selecting_a_step_brings_in_what_it_depends_on()
    {
        var (plan, _) = FabrikamPlan();
        var step = plan.Steps.First(s => s.Operation == StepOperation.AssociateWithHub && s.DependsOn.Any(d => d.StartsWith("register-", StringComparison.Ordinal)));
        var ids = plan.WithDependencies([step.Id]).Select(s => s.Id).ToList();
        Assert.Contains(step.DependsOn[0], ids);
        Assert.Contains(step.Id, ids);
    }

    [Fact]
    public async Task The_runner_skips_what_exists_passes_new_hub_ids_on_and_blocks_after_a_failure()
    {
        var plan = new ImplementationPlan { RootUrl = "https://contoso.sharepoint.com", Tenant = "contoso" };
        plan.Steps.AddRange(
        [
            new ImplementationStep { Id = "old", Kind = StepKind.Automatic, Operation = StepOperation.CreateSite, Target = "https://contoso.sharepoint.com/sites/old", Parameters = { ["title"] = "Old", ["url"] = "https://contoso.sharepoint.com/sites/old", ["webTemplate"] = "SITEPAGEPUBLISHING#0" } },
            new ImplementationStep { Id = "hr", Kind = StepKind.Automatic, Operation = StepOperation.CreateSite, Target = "https://contoso.sharepoint.com/sites/hr", Parameters = { ["title"] = "HR Hub", ["url"] = "https://contoso.sharepoint.com/sites/hr", ["webTemplate"] = "SITEPAGEPUBLISHING#0" } },
            new ImplementationStep { Id = "register-hr", Kind = StepKind.Automatic, Operation = StepOperation.RegisterHub, Target = "https://contoso.sharepoint.com/sites/hr", Parameters = { ["title"] = "HR Hub" }, DependsOn = ["hr"] },
            new ImplementationStep { Id = "join", Kind = StepKind.Automatic, Operation = StepOperation.AssociateWithHub, Target = "https://contoso.sharepoint.com/sites/benefits", Parameters = { ["hubSiteId"] = "{register-hr.hubSiteId}" }, DependsOn = ["register-hr"] },
            new ImplementationStep { Id = "bad", Kind = StepKind.Automatic, Operation = StepOperation.CreateSite, Target = "https://contoso.sharepoint.com/sites/bad", Parameters = { ["title"] = "Bad", ["url"] = "https://contoso.sharepoint.com/sites/bad", ["webTemplate"] = "SITEPAGEPUBLISHING#0" } },
            new ImplementationStep { Id = "after-bad", Kind = StepKind.Automatic, Operation = StepOperation.ApplyTheme, Target = "https://contoso.sharepoint.com/sites/bad", Parameters = { ["name"] = "T", ["themeJson"] = "{}" }, DependsOn = ["bad"] },
        ]);

        var sharePoint = new FakeSharePoint();
        var client = new HttpClient(sharePoint);
        var audit = new AuditLog(Path.Combine(Directory.CreateTempSubdirectory("hubwright-audit").FullName, "audit.jsonl"));
        var saves = 0;
        var runner = new ImplementationRunner(plan, () => saves++, new TenantHttp((_, _) => Task.FromResult("t"), client), new TenantWriter((_, _) => Task.FromResult("t"), client) { Log = (r, status, _) => audit.Append(status is null ? "write.send" : "write.result", r.Url) }, audit)
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
        };
        await runner.RunAsync(plan.Steps.Select(s => s.Id), null, CancellationToken.None);

        Assert.Equal(StepStatus.AlreadyDone, plan.Find("old")!.Status);
        Assert.Equal(StepStatus.Done, plan.Find("hr")!.Status);
        Assert.Equal(HubId, plan.Find("register-hr")!.Outputs["hubSiteId"]);
        Assert.Equal(StepStatus.Done, plan.Find("join")!.Status);
        Assert.Contains(sharePoint.Requests, r => r == $"POST /sites/benefits/_api/site/JoinHubSite('{HubId}')");
        Assert.Equal(StepStatus.Failed, plan.Find("bad")!.Status);
        Assert.Equal(StepStatus.Blocked, plan.Find("after-bad")!.Status);
        Assert.DoesNotContain(sharePoint.Requests, r => r.StartsWith("POST /_api/SPSiteManager/create", StringComparison.Ordinal) && r.Contains("/sites/old", StringComparison.Ordinal));
        Assert.True(saves > 0);
        Assert.Contains(audit.Read(), e => e.Action == "write.send");
        Assert.Equal(-1, audit.Verify());
    }

    [Theory]
    [InlineData("{register-hr-hub.hubSiteId}", true)]
    [InlineData("{hub-x.url}", true)]
    [InlineData("{\"isInverted\":false,\"palette\":{\"themePrimary\":\"#b127bd\"}}", false)]
    [InlineData("{}", false)]
    [InlineData("plain", false)]
    public void Only_exact_step_references_are_placeholders(string value, bool placeholder)
    {
        Assert.Equal(placeholder, ImplementationPlanner.IsPlaceholder(value));
        var plan = new ImplementationPlan();
        Assert.Equal(placeholder ? null : value, ImplementationPlanner.Resolve(plan, value));
    }

    [Fact]
    public async Task Theme_steps_send_the_theme_json_as_it_is()
    {
        var (plan, _) = FabrikamPlan();
        var sharePoint = new FakeSharePoint();
        var client = new HttpClient(sharePoint);
        var audit = new AuditLog(Path.Combine(Directory.CreateTempSubdirectory("hubwright-audit").FullName, "audit.jsonl"));
        var bodies = new List<string>();
        var writer = new TenantWriter((_, _) => Task.FromResult("t"), client) { Log = (r, status, _) => { if (status is null && r.Body is not null) { bodies.Add(r.Body); } } };
        var runner = new ImplementationRunner(plan, () => { }, new TenantHttp((_, _) => Task.FromResult("t"), client), writer, audit);
        await runner.RunAsync(["theme"], null, CancellationToken.None);
        Assert.Equal(StepStatus.Done, plan.Find("theme")!.Status);
        var sent = System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(bodies))!;
        Assert.Equal(plan.Find("theme")!.Parameters["themeJson"], sent["themeJson"]!.GetValue<string>());
        Assert.StartsWith("{\"isInverted\"", sent["themeJson"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_site_the_account_cannot_reach_fails_with_the_reason_and_a_by_hand_route()
    {
        var plan = new ImplementationPlan { RootUrl = "https://contoso.sharepoint.com", Tenant = "contoso" };
        plan.Steps.Add(new ImplementationStep { Id = "join", Title = "Associate \"Communications\" with Communications Hub", Kind = StepKind.Automatic, Operation = StepOperation.AssociateWithHub, Target = "https://contoso.sharepoint.com/sites/denied", Parameters = { ["hubSiteId"] = HubId } });
        var sharePoint = new FakeSharePoint();
        var client = new HttpClient(sharePoint);
        var audit = new AuditLog(Path.Combine(Directory.CreateTempSubdirectory("hubwright-audit").FullName, "audit.jsonl"));
        var runner = new ImplementationRunner(plan, () => { }, new TenantHttp((_, _) => Task.FromResult("t"), client), new TenantWriter((_, _) => Task.FromResult("t"), client), audit);
        await runner.RunAsync(["join"], null, CancellationToken.None);
        var step = plan.Find("join")!;
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Contains("SharePoint refused (403)", step.Result, StringComparison.Ordinal);
        Assert.Contains("no rights on https://contoso.sharepoint.com/sites/denied", step.Result, StringComparison.Ordinal);
        Assert.DoesNotContain(sharePoint.Requests, r => r.Contains("JoinHubSite", StringComparison.Ordinal));
        var fallback = ImplementationPlanner.Fallback(plan, step);
        Assert.NotNull(fallback);
        Assert.StartsWith("https://contoso-admin.sharepoint.com/", fallback.Value.Url, StringComparison.Ordinal);
        Assert.Contains(fallback.Value.Instructions, i => i.Contains("Active sites", StringComparison.Ordinal) && i.Contains("Communications Hub", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Checking_the_tenant_marks_what_is_in_place_and_never_writes()
    {
        var plan = new ImplementationPlan { RootUrl = "https://contoso.sharepoint.com", Tenant = "contoso" };
        plan.Steps.AddRange(
        [
            new ImplementationStep { Id = "old", Kind = StepKind.Automatic, Operation = StepOperation.CreateSite, Target = "https://contoso.sharepoint.com/sites/old", Parameters = { ["title"] = "Old", ["url"] = "https://contoso.sharepoint.com/sites/old", ["webTemplate"] = "SITEPAGEPUBLISHING#0" } },
            new ImplementationStep { Id = "register-old", Kind = StepKind.Automatic, Operation = StepOperation.RegisterHub, Target = "https://contoso.sharepoint.com/sites/old", Parameters = { ["title"] = "hr" }, DependsOn = ["old"] },
            new ImplementationStep { Id = "join-old", Kind = StepKind.Automatic, Operation = StepOperation.AssociateWithHub, Target = "https://contoso.sharepoint.com/sites/benefits", Parameters = { ["hubSiteId"] = "{register-old.hubSiteId}" }, DependsOn = ["register-old"] },
            new ImplementationStep { Id = "hr", Kind = StepKind.Automatic, Operation = StepOperation.CreateSite, Target = "https://contoso.sharepoint.com/sites/hr", Parameters = { ["title"] = "HR Hub", ["url"] = "https://contoso.sharepoint.com/sites/hr", ["webTemplate"] = "SITEPAGEPUBLISHING#0" } },
            new ImplementationStep { Id = "register-hr", Kind = StepKind.Automatic, Operation = StepOperation.RegisterHub, Target = "https://contoso.sharepoint.com/sites/hr", Parameters = { ["title"] = "HR Hub" }, DependsOn = ["hr"] },
            new ImplementationStep { Id = "join-hr", Kind = StepKind.Automatic, Operation = StepOperation.AssociateWithHub, Target = "https://contoso.sharepoint.com/sites/people", Parameters = { ["hubSiteId"] = "{register-hr.hubSiteId}" }, DependsOn = ["register-hr"] },
            new ImplementationStep { Id = "nav", Kind = StepKind.Automatic, Operation = StepOperation.BuildNavigation, Target = "https://contoso.sharepoint.com/sites/old", Parameters = { ["nav"] = "[]" } },
        ]);
        var sharePoint = new FakeSharePoint();
        sharePoint.Hub("https://contoso.sharepoint.com/sites/old");
        sharePoint.Join("https://contoso.sharepoint.com/sites/benefits");
        var client = new HttpClient(sharePoint);
        var audit = new AuditLog(Path.Combine(Directory.CreateTempSubdirectory("hubwright-audit").FullName, "audit.jsonl"));
        var runner = new ImplementationRunner(plan, () => { }, new TenantHttp((_, _) => Task.FromResult("t"), client), new TenantWriter((_, _) => throw new InvalidOperationException("no writes in a check"), client), audit);

        var found = await runner.CheckAsync(plan.Steps.Select(s => s.Id), null, CancellationToken.None);

        Assert.Equal(3, found);
        Assert.All(new[] { "old", "register-old", "join-old" }, id => Assert.Equal(StepStatus.AlreadyDone, plan.Find(id)!.Status));
        Assert.Equal(HubId, plan.Find("register-old")!.Outputs["hubSiteId"]);
        Assert.All(new[] { "hr", "register-hr", "join-hr", "nav" }, id => Assert.Equal(StepStatus.Pending, plan.Find(id)!.Status));
        Assert.Equal("Not created yet.", plan.Find("hr")!.Result);
        Assert.Contains("Checked when it runs", plan.Find("nav")!.Result, StringComparison.Ordinal);
        Assert.All(sharePoint.Requests, r => Assert.StartsWith("GET ", r, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_check_takes_applied_themes_and_steps_done_by_hand_from_the_audit_log()
    {
        var plan = new ImplementationPlan { RootUrl = "https://contoso.sharepoint.com", Tenant = "contoso" };
        plan.Steps.AddRange(
        [
            new ImplementationStep { Id = "theme", Kind = StepKind.Automatic, Operation = StepOperation.AddOrUpdateTheme, Status = StepStatus.AlreadyDone },
            new ImplementationStep { Id = "hr-theme", Kind = StepKind.Automatic, Operation = StepOperation.ApplyTheme, Target = "https://contoso.sharepoint.com/sites/hr" },
            new ImplementationStep { Id = "it-theme", Kind = StepKind.Automatic, Operation = StepOperation.ApplyTheme, Target = "https://contoso.sharepoint.com/sites/it" },
            new ImplementationStep { Id = "new-theme", Kind = StepKind.Automatic, Operation = StepOperation.ApplyTheme, Target = "https://contoso.sharepoint.com/sites/new" },
            new ImplementationStep { Id = "global-nav", Kind = StepKind.Guided },
        ]);
        var audit = new AuditLog(Path.Combine(Directory.CreateTempSubdirectory("hubwright-audit").FullName, "audit.jsonl"));
        audit.Append("implement.step", "contoso · theme · Done · Theme added to the tenant.");
        audit.Append("implement.step", "contoso · hr-theme · Done · Theme applied.");
        audit.Append("implement.step", "other · new-theme · Done · Theme applied.");
        audit.Append("implement.step", "contoso · theme · Done · Theme updated.");
        audit.Append("implement.step", "contoso · it-theme · Done · Theme applied.");
        audit.Append("implement.done", "contoso · global-nav · Turn on global navigation");
        var client = new HttpClient(new FakeSharePoint());
        var runner = new ImplementationRunner(plan, () => { }, new TenantHttp((_, _) => Task.FromResult("t"), client), new TenantWriter((_, _) => throw new InvalidOperationException("no writes"), client), audit);

        await runner.CheckAsync(plan.Steps.Select(s => s.Id), null, CancellationToken.None);

        // hr was applied before the theme was updated, so it needs the new colours; no run for "new" on this tenant.
        Assert.Equal(StepStatus.Pending, plan.Find("hr-theme")!.Status);
        Assert.Equal(StepStatus.AlreadyDone, plan.Find("it-theme")!.Status);
        Assert.Equal(StepStatus.Pending, plan.Find("new-theme")!.Status);
        Assert.Equal(StepStatus.Done, plan.Find("global-nav")!.Status);
    }

    [Fact]
    public void Rebuilding_keeps_unchanged_finished_steps_and_resets_the_rest()
    {
        var (before, _) = FabrikamPlan();
        var (after, _) = FabrikamPlan();
        var site = before.Steps.First(s => s.Operation == StepOperation.CreateSite);
        site.Status = StepStatus.Done;
        site.Outputs["url"] = site.Target!;
        before.Find("theme")!.Status = StepStatus.Done;
        before.Find("theme")!.Parameters["themeJson"] = "{\"palette\":{\"themePrimary\":\"#000000\"}}";
        before.Find("sharing")!.Status = StepStatus.Failed;

        Assert.Equal(1, Implementations.Carry(before, after));
        Assert.Equal(StepStatus.Done, after.Find(site.Id)!.Status);
        Assert.Equal(site.Target, after.Find(site.Id)!.Outputs["url"]);
        Assert.Equal(StepStatus.Pending, after.Find("theme")!.Status);
        Assert.Equal(StepStatus.Pending, after.Find("sharing")!.Status);
    }

    [Theory]
    [InlineData("{\"palette\":{\"themePrimary\":\"#0F6CBD\",\"white\":\"#ffffff\"}}", "{\"isInverted\":false,\"palette\":{\"white\":\"#FFFFFF\",\"themePrimary\":\"#0f6cbd\"}}", true)]
    [InlineData("{\"palette\":{\"themePrimary\":\"#0f6cbd\"}}", "{\"palette\":{\"themePrimary\":\"#5b2c83\"}}", false)]
    [InlineData(null, "{\"palette\":{}}", false)]
    public void Themes_compare_by_palette(string? a, string b, bool same)
        => Assert.Equal(same, ImplementationRunner.SamePalette(a, b));

    [Fact]
    public void Preview_lists_the_requests_and_sends_nothing()
    {
        var (plan, _) = FabrikamPlan();
        var preview = Implementations.Preview(plan, ["sharing", "theme"]);
        Assert.Contains(preview, p => p.Request.Method == "PATCH" && p.Request.Url.EndsWith("/admin/sharepoint/settings", StringComparison.Ordinal));
        Assert.Contains(preview, p => p.Request.Url.EndsWith("/_api/thememanager/AddTenantTheme", StringComparison.Ordinal));
    }

    /// <summary>A small SharePoint: /sites/old exists, /sites/bad fails to create, new hubs get <see cref="HubId"/>.</summary>
    private sealed class FakeSharePoint : HttpMessageHandler
    {
        private readonly HashSet<string> _sites = new(StringComparer.OrdinalIgnoreCase) { "https://contoso.sharepoint.com/sites/old" };
        private readonly HashSet<string> _hubs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _siteHub = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requests { get; } = [];

        /// <summary>An existing hub (titled "hr", as GetById answers).</summary>
        public void Hub(string url)
        {
            _sites.Add(url);
            _hubs.Add(url);
        }

        /// <summary>A site already in the hub.</summary>
        public void Join(string url) => _siteHub[url] = HubId;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add($"{request.Method} {path}{(path.Contains("JoinHubSite", StringComparison.Ordinal) ? "" : "")}");
            static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (path.StartsWith("/sites/denied", StringComparison.OrdinalIgnoreCase))
            {
                return Json("{\"odata.error\":{\"code\":\"-2147024891, System.UnauthorizedAccessException\",\"message\":{\"value\":\"Attempted to perform an unauthorized operation.\"}}}", HttpStatusCode.Forbidden);
            }


            if (path.EndsWith("/_api/SPSiteManager/status", StringComparison.OrdinalIgnoreCase))
            {
                var url = query.Split("url='")[1].TrimEnd('\'');
                return Json($"{{\"SiteStatus\":{(_sites.Contains(url) ? 2 : 0)}}}");
            }

            if (path.EndsWith("/_api/SPSiteManager/create", StringComparison.OrdinalIgnoreCase))
            {
                if (body.Contains("/sites/bad", StringComparison.Ordinal))
                {
                    return Json("{\"error\":\"quota\"}", HttpStatusCode.BadRequest);
                }

                var url = System.Text.Json.Nodes.JsonNode.Parse(body)!["request"]!["Url"]!.GetValue<string>();
                _sites.Add(url);
                return Json("{\"SiteStatus\":2}");
            }

            if (path.EndsWith("/_api/HubSites", StringComparison.OrdinalIgnoreCase))
            {
                return Json("{\"value\":[" + string.Join(",", _hubs.Select(h => $"{{\"ID\":\"{HubId}\",\"SiteUrl\":\"{h}\"}}")) + "]}");
            }

            if (path.EndsWith("/_api/site/RegisterHubSite", StringComparison.OrdinalIgnoreCase))
            {
                _hubs.Add("https://contoso.sharepoint.com" + path[..path.IndexOf("/_api", StringComparison.Ordinal)]);
                return Json($"{{\"ID\":\"{HubId}\"}}");
            }

            if (path.EndsWith("/_api/HubSites/GetById", StringComparison.OrdinalIgnoreCase))
            {
                return request.Method == HttpMethod.Get ? Json("{\"Title\":\"hr\"}") : new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.EndsWith("/_api/site", StringComparison.OrdinalIgnoreCase))
            {
                var site = "https://contoso.sharepoint.com" + path[..path.IndexOf("/_api", StringComparison.Ordinal)];
                return Json($"{{\"HubSiteId\":\"{_siteHub.GetValueOrDefault(site, "00000000-0000-0000-0000-000000000000")}\"}}");
            }

            if (path.Contains("/_api/site/JoinHubSite", StringComparison.OrdinalIgnoreCase))
            {
                var site = "https://contoso.sharepoint.com" + path[..path.IndexOf("/_api", StringComparison.Ordinal)];
                _siteHub[site] = path.Split("'")[1];
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
