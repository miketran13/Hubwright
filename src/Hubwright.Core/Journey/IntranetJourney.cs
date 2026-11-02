using Hubwright.Scoring;
using Hubwright.Snapshot;

namespace Hubwright.Journey;

public enum StageState { NotStarted, InProgress, Done }

/// <summary>One stage of the intranet journey: where it stands and the one thing to do next.</summary>
/// <param name="Page">The desktop page tag that does the next action.</param>
/// <param name="Command">The CLI command that does the next action.</param>
public sealed record JourneyStage(string Id, string Title, string Purpose, StageState State, string Status, string NextAction, string Page, string Command);

/// <summary>What the journey is computed from. The engine gathers it from disk; nothing here does I/O.</summary>
public sealed record JourneyInputs
{
    /// <summary>An Entra app (client id) is saved in the configuration.</summary>
    public bool AppRegistered { get; init; }

    /// <summary>The configured tenant this workspace belongs to (null for samples, imports and designs).</summary>
    public string? ConnectedTenant { get; init; }

    public string Tenant { get; init; } = "";

    public TenantSnapshot? Snapshot { get; init; }

    public Assessment? Assessment { get; init; }

    public int SnapshotCount { get; init; }

    /// <summary>An intranet proposal or a blueprint exists for this tenant.</summary>
    public bool HasPlan { get; init; }

    public int BuildSteps { get; init; }

    public int BuildFinished { get; init; }

    /// <summary>Settings in the most recent security baseline plan (0 when none was made).</summary>
    public int BaselineSteps { get; init; }

    public int BaselineFinished { get; init; }

    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Connect → Assess → Plan → Build → Secure → Run. Each stage's state comes from what is on disk,
/// so the Home page and <c>hubwright status</c> always agree.
/// </summary>
public static class IntranetJourney
{
    /// <summary>Rule categories the Secure stage watches.</summary>
    public static readonly IReadOnlyList<string> SecurityCategories = ["sharing", "permissions", "copilot-readiness"];

    /// <summary>Hubwright's own judgment (not a Microsoft limit): a snapshot older than this is stale.</summary>
    public static readonly TimeSpan FreshScan = TimeSpan.FromDays(30);

    public static IReadOnlyList<JourneyStage> Stages(JourneyInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        return [Connect(inputs), Assess(inputs), Plan(inputs), Build(inputs), Secure(inputs), Run(inputs)];
    }

    /// <summary>The first stage that isn't done, or null when every stage is.</summary>
    public static JourneyStage? Next(IReadOnlyList<JourneyStage> stages) => stages?.FirstOrDefault(s => s.State != StageState.Done);

    private static bool Offline(JourneyInputs i) => i.Snapshot?.Source is SnapshotSource.Sample or SnapshotSource.Import or SnapshotSource.Blueprint;

    private static JourneyStage Connect(JourneyInputs i)
    {
        const string purpose = "Register Hubwright's read-only app in Entra ID and sign in as a SharePoint Administrator.";
        if (Offline(i))
        {
            return new("connect", "Connect", purpose, StageState.Done, $"Not needed: {i.Tenant} is a {i.Snapshot!.Source.ToString().ToLowerInvariant()} workspace.", "Connect a real tenant when you're ready.", "setup", "hubwright setup");
        }

        if (!i.AppRegistered)
        {
            return new("connect", "Connect", purpose, StageState.NotStarted, "No app registered yet.", "Register the app (read-only) and sign in.", "setup", "hubwright setup");
        }

        return string.IsNullOrEmpty(i.ConnectedTenant)
            ? new("connect", "Connect", purpose, StageState.InProgress, "The app is registered; nobody has signed in yet.", "Sign in with your browser.", "setup", "hubwright login")
            : new("connect", "Connect", purpose, StageState.Done, $"Connected to {i.ConnectedTenant}.sharepoint.com.", "Nothing to do.", "setup", "hubwright setup");
    }

    private static JourneyStage Assess(JourneyInputs i)
    {
        const string purpose = "Scan the tenant and check it against Microsoft's intranet guidance.";
        if (i.Snapshot is null || i.Assessment is null)
        {
            return new("assess", "Assess", purpose, StageState.NotStarted, "No scan yet.", "Scan the tenant (read-only).", "setup", "hubwright scan");
        }

        var a = i.Assessment;
        var grade = Assessment.Grade(a.Overall);
        var status = $"Grade {grade} · {a.Findings.Count} finding(s) · scanned {i.Snapshot.CapturedAt:yyyy-MM-dd}.";
        return a.Findings.Count == 0
            ? new("assess", "Assess", purpose, StageState.Done, status, "Nothing to fix.", "dashboard", "hubwright assess")
            : new("assess", "Assess", purpose, StageState.InProgress, status, "Read the findings, highest severity first.", "findings", "hubwright findings");
    }

    private static JourneyStage Plan(JourneyInputs i)
    {
        const string purpose = "Decide the target intranet: hubs, sites, navigation, look and policies.";
        if (i.HasPlan)
        {
            return new("plan", "Plan", purpose, StageState.Done, "A plan exists.", "Review it with your stakeholders in the site preview.", "proposal", "hubwright proposal show --open");
        }

        return i.Snapshot is null
            ? new("plan", "Plan", purpose, StageState.NotStarted, "No plan yet.", "Design from scratch, or scan first to plan from what you have.", "home", "hubwright blueprint new")
            : new("plan", "Plan", purpose, StageState.NotStarted, "No plan yet.", "Answer a few questions to get an intranet proposal.", "proposal", "hubwright proposal new");
    }

    private static JourneyStage Build(JourneyInputs i)
    {
        const string purpose = "Create the sites, hubs, navigation and look, with documented APIs, after you confirm.";
        if (i.BuildSteps == 0)
        {
            return new("build", "Build", purpose, StageState.NotStarted, i.HasPlan ? "Not started." : "Needs a plan first.", i.HasPlan ? "Open the build plan and run a dry run." : "Make a plan first.", i.HasPlan ? "build" : "proposal", i.HasPlan ? "hubwright build" : "hubwright proposal new");
        }

        var status = $"{i.BuildFinished} of {i.BuildSteps} steps finished.";
        return i.BuildFinished >= i.BuildSteps
            ? new("build", "Build", purpose, StageState.Done, status, "Nothing to do.", "build", "hubwright build")
            : new("build", "Build", purpose, StageState.InProgress, status, "Continue with the next steps.", "build", "hubwright build");
    }

    private static JourneyStage Secure(JourneyInputs i)
    {
        const string purpose = "Sharing, guests, permissions and Copilot oversharing, set the way Microsoft recommends.";
        if (i.Assessment is null)
        {
            return new("secure", "Secure", purpose, StageState.NotStarted, "Needs a scan.", "Scan the tenant first.", "setup", "hubwright scan");
        }

        var watched = i.Assessment.Categories.Where(c => SecurityCategories.Contains(c.Category, StringComparer.OrdinalIgnoreCase)).ToList();
        var failed = watched.Sum(c => c.Failed);
        var unknown = watched.Sum(c => c.NotAssessed);
        var baselineDone = i.BaselineSteps > 0 && i.BaselineFinished >= i.BaselineSteps;
        var status = failed > 0 ? $"{failed} security check(s) failed" : "No failed security checks";
        status += unknown > 0 ? $"; {unknown} couldn't be read (not a pass)" : "";
        status += i.BaselineSteps > 0 ? $"; baseline: {i.BaselineFinished} of {i.BaselineSteps} settings in place." : "; no baseline applied yet.";
        if (failed == 0 && unknown == 0 && baselineDone)
        {
            return new("secure", "Secure", purpose, StageState.Done, status, "Nothing to do.", "secure", "hubwright secure");
        }

        return i.BaselineSteps > 0 && !baselineDone
            ? new("secure", "Secure", purpose, StageState.InProgress, status, "Continue the security baseline.", "baseline", "hubwright secure plan")
            : new("secure", "Secure", purpose, failed == 0 && unknown == 0 && i.BaselineSteps == 0 ? StageState.NotStarted : StageState.InProgress, status, i.BaselineSteps == 0 ? "Apply the security baseline." : "Fix the remaining security findings.", i.BaselineSteps == 0 ? "baseline" : "secure", i.BaselineSteps == 0 ? "hubwright secure baseline" : "hubwright secure");
    }

    private static JourneyStage Run(JourneyInputs i)
    {
        const string purpose = "Scan regularly, watch the score and catch drift from the plan.";
        if (i.Snapshot is null)
        {
            return new("run", "Run", purpose, StageState.NotStarted, "No scans yet.", "Scan the tenant.", "setup", "hubwright scan");
        }

        var age = i.Now - i.Snapshot.CapturedAt;
        var status = $"{i.SnapshotCount} scan(s); the latest is {Math.Max(0, (int)age.TotalDays)} day(s) old.";
        if (age > FreshScan)
        {
            return new("run", "Run", purpose, StageState.InProgress, status, "Scan again to see what changed.", "setup", "hubwright scan");
        }

        return i.SnapshotCount >= 2
            ? new("run", "Run", purpose, StageState.Done, status, "Check drift and the trend; schedule weekly scans.", "monitor", "hubwright drift")
            : new("run", "Run", purpose, StageState.InProgress, status, "Scan again after changes to compare.", "monitor", "hubwright scan");
    }
}
