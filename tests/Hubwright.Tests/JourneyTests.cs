using Hubwright.Engine;
using Hubwright.Journey;
using Hubwright.Rules;
using Hubwright.Scoring;
using Hubwright.Snapshot;

namespace Hubwright.Tests;

public class JourneyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static Assessment Run(TenantSnapshot snapshot)
    {
        Values.Now = Now;
        var library = SkillTests.BuiltIn();
        return Assessment.Run(snapshot, library.Rules, library.Versions, null);
    }

    private static JourneyStage Stage(JourneyInputs inputs, string id) => IntranetJourney.Stages(inputs).Single(s => s.Id == id);

    [Fact]
    public void A_new_install_starts_at_connect_and_every_stage_is_to_do()
    {
        var stages = IntranetJourney.Stages(new JourneyInputs { Tenant = "sample", Now = Now });
        Assert.Equal(["connect", "assess", "plan", "build", "secure", "run"], stages.Select(s => s.Id));
        Assert.All(stages, s => Assert.Equal(StageState.NotStarted, s.State));
        Assert.Equal("connect", IntranetJourney.Next(stages)!.Id);
        Assert.Equal("setup", IntranetJourney.Next(stages)!.Page);
    }

    [Fact]
    public void Connect_moves_from_registered_to_signed_in()
    {
        Assert.Equal(StageState.InProgress, Stage(new JourneyInputs { AppRegistered = true, Now = Now }, "connect").State);
        Assert.Equal(StageState.Done, Stage(new JourneyInputs { AppRegistered = true, ConnectedTenant = "contoso", Now = Now }, "connect").State);
    }

    [Fact]
    public void A_sample_needs_no_connection_and_points_at_the_findings()
    {
        var snapshot = SampleTenants.Fabrikam(Now);
        var stages = IntranetJourney.Stages(new JourneyInputs { Tenant = "fabrikam", Snapshot = snapshot, Assessment = Run(snapshot), SnapshotCount = 1, Now = Now });
        Assert.Equal(StageState.Done, stages[0].State);
        Assert.Equal(StageState.InProgress, stages[1].State);
        Assert.Equal("assess", IntranetJourney.Next(stages)!.Id);
        Assert.Equal("findings", IntranetJourney.Next(stages)!.Page);
    }

    [Fact]
    public void Secure_watches_sharing_permissions_and_copilot_and_unknown_is_not_a_pass()
    {
        var messy = SampleTenants.Fabrikam(Now);
        var secure = Stage(new JourneyInputs { Snapshot = messy, Assessment = Run(messy), Now = Now }, "secure");
        Assert.Equal(StageState.InProgress, secure.State);
        Assert.Contains("failed", secure.Status, StringComparison.Ordinal);

        var applied = Stage(new JourneyInputs { Snapshot = messy, Assessment = Run(messy), BaselineSteps = 15, BaselineFinished = 3, Now = Now }, "secure");
        Assert.Equal("baseline", applied.Page);
        Assert.Contains("3 of 15", applied.Status, StringComparison.Ordinal);

        // A snapshot with nothing read about sharing must not show Secure as done.
        var empty = new TenantSnapshot { Tenant = "blank", CapturedAt = Now };
        var unknown = Stage(new JourneyInputs { Snapshot = empty, Assessment = Run(empty), Now = Now }, "secure");
        Assert.NotEqual(StageState.Done, unknown.State);
    }

    [Fact]
    public void Build_counts_finished_steps_and_run_wants_a_fresh_second_scan()
    {
        var snapshot = SampleTenants.Northwind(Now);
        snapshot.Source = SnapshotSource.Scan;
        var inputs = new JourneyInputs { AppRegistered = true, ConnectedTenant = "northwind", Snapshot = snapshot, Assessment = Run(snapshot), SnapshotCount = 1, HasPlan = true, BuildSteps = 10, BuildFinished = 4, Now = Now };
        Assert.Equal(StageState.InProgress, Stage(inputs, "build").State);
        Assert.Equal(StageState.Done, Stage(inputs with { BuildFinished = 10 }, "build").State);
        Assert.Equal(StageState.InProgress, Stage(inputs, "run").State);
        Assert.Equal(StageState.Done, Stage(inputs with { SnapshotCount = 2 }, "run").State);
        Assert.Equal(StageState.InProgress, Stage(inputs with { SnapshotCount = 2, Now = Now.AddDays(45) }, "run").State);
    }

    [Fact]
    public void Every_stage_points_at_a_desktop_page_and_a_cli_command()
    {
        var desktopTags = new[] { "home", "setup", "dashboard", "findings", "structure", "reports", "proposal", "blueprint", "theme-editor", "preview", "build", "secure", "baseline", "history", "monitor", "owners" };
        var snapshot = SampleTenants.Fabrikam(Now);
        foreach (var inputs in new[] { new JourneyInputs { Now = Now }, new JourneyInputs { Snapshot = snapshot, Assessment = Run(snapshot), HasPlan = true, BuildSteps = 2, SnapshotCount = 2, AppRegistered = true, ConnectedTenant = "x", Now = Now } })
        {
            Assert.All(IntranetJourney.Stages(inputs), s =>
            {
                Assert.Contains(s.Page, desktopTags);
                Assert.StartsWith("hubwright ", s.Command, StringComparison.Ordinal);
            });
        }
    }
}
