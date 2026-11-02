using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Hubwright.Storage;
using Hubwright.Engine;
using Hubwright.Http;
using Hubwright.Monitoring;
using Hubwright.Implementation;
using Hubwright.Security;
using Hubwright.Snapshot;
using Hubwright.Tools;

namespace Hubwright.Tests;

public class SecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<BaselineSetting> Baseline() => SkillTests.BuiltIn().Baseline;

    [Fact]
    public void Every_baseline_setting_is_cited_explained_and_inside_the_write_allow_lists()
    {
        var library = SkillTests.BuiltIn();
        var hints = library.Hints;
        Assert.NotEmpty(library.Baseline);
        Assert.All(library.Baseline, s =>
        {
            Assert.True(s.Hint is not null && hints.ContainsKey(s.Hint), $"{s.Id} has no hint");
            Assert.False(string.IsNullOrWhiteSpace(s.Quote) && s.Basis == Rules.Basis.Documented, $"{s.Id} is documented without a quote");
            switch (s.Apply)
            {
                case BaselineApply.Graph:
                    Assert.Contains(s.Property, TenantWriter.GraphSettings);
                    break;
                case BaselineApply.Pnp:
                    Assert.Contains(s.Property, PnpScripts.TenantParameters);
                    break;
                default:
                    Assert.False(string.IsNullOrWhiteSpace(s.GuideUrl));
                    break;
            }
        });
    }

    [Fact]
    public void Fabrikam_differs_from_the_standard_profile_and_unread_settings_are_not_a_pass()
    {
        var items = SecurityBaseline.Compare(SampleTenants.Fabrikam(Now).Settings, Baseline(), BaselineProfile.Standard);
        Assert.Equal(BaselineState.Differs, items.Single(i => i.Setting.Id == "sharing-level").State);
        Assert.Equal(BaselineState.NotRead, items.Single(i => i.Setting.Id == "legacy-auth").State);
        Assert.DoesNotContain(items, i => i.Setting.Id == "idle-sign-out");
        Assert.Contains(SecurityBaseline.Compare(SampleTenants.Fabrikam(Now).Settings, Baseline(), BaselineProfile.Strict), i => i.Setting.Id == "idle-sign-out");
    }

    [Fact]
    public void A_shorter_expiry_already_meets_the_baseline_and_is_never_loosened()
    {
        var days = Baseline().Single(s => s.Id == "guest-expiration-days");
        Assert.True(SecurityBaseline.Same("30", "90", days));
        Assert.False(SecurityBaseline.Same("120", "90", days));
        Assert.False(SecurityBaseline.Same("0", "90", days));
    }

    [Fact]
    public void The_plan_marks_what_is_in_place_orders_guest_expiry_and_restore_puts_values_back()
    {
        var snapshot = SampleTenants.Fabrikam(Now);
        snapshot.Settings.CustomScriptAllowed = false;
        var plan = SecurityBaseline.Plan(snapshot, Baseline(), BaselineProfile.Standard, "https://fabrikam.sharepoint.com");
        Assert.Equal(StepStatus.AlreadyDone, plan.Find("baseline-custom-script")!.Status);
        Assert.Contains("baseline-guest-expiration-required", plan.Find("baseline-guest-expiration-days")!.DependsOn);
        Assert.All(plan.Steps, s => Assert.NotNull(s.Hint));
        Assert.Equal(StepOperation.SetTenantSetting, plan.Find("baseline-sharing-level")!.Operation);
        Assert.Equal("https://fabrikam-admin.sharepoint.com", plan.Find("baseline-default-link-type")!.Target);

        var link = plan.Find("baseline-default-link-type")!;
        link.Status = StepStatus.Done;
        link.Outputs["previous"] = "AnonymousAccess";
        var restore = SecurityBaseline.Restore(plan);
        var back = Assert.Single(restore.Steps);
        Assert.Equal("AnonymousAccess", back.Parameters["value"]);
        Assert.Equal("DefaultSharingLinkType", back.Parameters["parameter"]);
    }

    [Fact]
    public void Set_PnPTenant_scripts_read_first_and_only_use_allow_listed_parameters_and_values()
    {
        var plan = SecurityBaseline.Plan(SampleTenants.Fabrikam(Now), Baseline(), BaselineProfile.Standard, "https://fabrikam.sharepoint.com");
        var script = PnpScripts.Script(plan, plan.Find("baseline-anyone-link-expiry")!);
        Assert.Contains("Get-PnPTenant", script, StringComparison.Ordinal);
        Assert.Contains("Set-PnPTenant -RequireAnonymousLinksExpireInDays 30", script, StringComparison.Ordinal);
        Assert.Contains("-le 30", script, StringComparison.Ordinal);
        Assert.Contains("https://fabrikam-admin.sharepoint.com", script, StringComparison.Ordinal);
        PnpShell.CheckTenantParameters(script);

        Assert.Throws<InvalidOperationException>(() => PnpShell.CheckTenantParameters("Set-PnPTenant -SharingCapability Disabled"));
        Assert.Throws<InvalidOperationException>(() => PnpScripts.Literal("7; Remove-PnPSite", "int"));
        Assert.Throws<InvalidOperationException>(() => PnpScripts.Literal("View -Force", "string"));
        var bad = plan.Find("baseline-default-link-type")!;
        bad.Parameters["parameter"] = "SharingCapability";
        Assert.Throws<InvalidOperationException>(() => PnpScripts.Script(plan, bad));
    }

    [Fact]
    public async Task The_writer_refuses_Graph_settings_outside_the_baseline()
    {
        var writer = new TenantWriter((_, _) => Task.FromResult("t")) { DryRun = true };
        await writer.SendAsync("PATCH", "https://graph.microsoft.com/v1.0/admin/sharepoint/settings", "{\"isLegacyAuthProtocolsEnabled\":false}", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SendAsync("PATCH", "https://graph.microsoft.com/v1.0/admin/sharepoint/settings", "{\"personalSiteDefaultStorageLimitInMB\":1}", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SendAsync("PATCH", "https://graph.microsoft.com/v1.0/admin/sharepoint/settings", "{}", CancellationToken.None));
    }

    [Fact]
    public async Task Graph_settings_are_read_first_recorded_and_then_changed_and_PnP_steps_record_the_old_value()
    {
        var plan = SecurityBaseline.Plan(SampleTenants.Fabrikam(Now), Baseline(), BaselineProfile.Strict, "https://fabrikam.sharepoint.com");
        var graph = new FakeGraph();
        var client = new HttpClient(graph);
        var audit = new AuditLog(Path.Combine(Directory.CreateTempSubdirectory("hubwright-secure").FullName, "audit.jsonl"));
        var pnp = new FakePnp();
        var runner = new ImplementationRunner(plan, () => { }, new TenantHttp((_, _) => Task.FromResult("t"), client), new TenantWriter((_, _) => Task.FromResult("t"), client), audit)
        {
            Pnp = pnp,
            Tokens = (_, _) => Task.FromResult("t"),
        };

        // A check reads and never writes.
        await runner.CheckAsync(["baseline-legacy-auth", "baseline-guest-same-account"], null, CancellationToken.None);
        Assert.Equal(StepStatus.AlreadyDone, plan.Find("baseline-guest-same-account")!.Status);
        Assert.Equal(StepStatus.Pending, plan.Find("baseline-legacy-auth")!.Status);
        Assert.Empty(graph.Patches);

        await runner.RunAsync(["baseline-legacy-auth", "baseline-idle-sign-out", "baseline-default-link-type"], null, CancellationToken.None);
        Assert.Equal(StepStatus.Done, plan.Find("baseline-legacy-auth")!.Status);
        Assert.Equal("true", plan.Find("baseline-legacy-auth")!.Outputs["previous"]);
        Assert.Contains(graph.Patches, p => p == "{\"isLegacyAuthProtocolsEnabled\":false}");
        Assert.Contains(graph.Patches, p => p.Contains("\"idleSessionSignOut\":{\"isEnabled\":true", StringComparison.Ordinal));
        Assert.Equal(StepStatus.Done, plan.Find("baseline-default-link-type")!.Status);
        Assert.Equal("AnonymousAccess", plan.Find("baseline-default-link-type")!.Outputs["previous"]);

        var restore = SecurityBaseline.Restore(plan);
        Assert.Contains(restore.Steps, s => s.Parameters["value"] == "true" && s.Parameters["property"] == "isLegacyAuthProtocolsEnabled");
    }

    private sealed class FakeGraph : HttpMessageHandler
    {
        private readonly JsonObject _settings = new()
        {
            ["sharingCapability"] = "externalUserAndGuestSharing",
            ["isLegacyAuthProtocolsEnabled"] = true,
            ["isRequireAcceptingUserToMatchInvitedUserEnabled"] = true,
            ["idleSessionSignOut"] = new JsonObject { ["isEnabled"] = false, ["warnAfterInSeconds"] = 0, ["signOutAfterInSeconds"] = 0 },
        };

        public List<string> Patches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Patch)
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Patches.Add(body);
                foreach (var (key, value) in JsonNode.Parse(body)!.AsObject())
                {
                    _settings[key] = value?.DeepClone();
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_settings.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakePnp : IPnpShell
    {
        public Task<PnpRun> RunAsync(string script, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
        {
            PnpShell.CheckTenantParameters(script);
            return Task.FromResult(new PnpRun(0, $"{PnpScripts.PreviousMarker} AnonymousAccess\n{PnpScripts.ResultMarker} Changed from AnonymousAccess to Internal.\n", ""));
        }
    }
}

public class MonitoringTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Drift_is_a_difference_on_something_that_was_built_not_work_still_to_do()
    {
        var snapshot = SampleTenants.Fabrikam(Now);
        var skills = SkillTests.BuiltIn();
        var applied = SecurityBaseline.Plan(snapshot, skills.Baseline, BaselineProfile.Standard, "https://fabrikam.sharepoint.com");
        var drift = Drift.Check(snapshot, null, null, skills.Baseline, applied);
        Assert.Contains(drift, d => d.Title == "External sharing level" && d.State == DriftState.NotBuilt);

        applied.Find("baseline-sharing-level")!.Status = StepStatus.Done;
        drift = Drift.Check(snapshot, null, null, skills.Baseline, applied);
        Assert.Contains(drift, d => d.Title == "External sharing level" && d.State == DriftState.Drifted);

        snapshot.Settings.SharingLevel = SharingLevel.NewAndExistingGuests;
        drift = Drift.Check(snapshot, null, null, skills.Baseline, applied);
        Assert.Contains(drift, d => d.Title == "External sharing level" && d.State == DriftState.InPlace);

        snapshot.Settings.SharingLevel = null;
        drift = Drift.Check(snapshot, null, null, skills.Baseline, applied);
        Assert.Contains(drift, d => d.Title == "External sharing level" && d.State == DriftState.NotRead);
    }

    [Fact]
    public void Owner_lists_and_csv_exports_defuse_spreadsheet_formulas()
    {
        var snapshot = SampleTenants.Fabrikam(Now);
        snapshot.Sites[0].Title = "=HYPERLINK(\"http://evil\")";
        snapshot.Sites[0].OwnerCount = 0;
        var rows = OwnerReport.For(snapshot);
        Assert.Equal(0, rows[0].Owners);
        var csv = CsvExport.Owners(rows);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\")\"", csv, StringComparison.Ordinal);
        Assert.StartsWith("﻿", csv, StringComparison.Ordinal);
    }
}
