using Hubwright.Implementation;
using Hubwright.Journey;

namespace Hubwright.Engine;

/// <summary>Reads the workspace from disk and computes the journey (Home page, <c>hubwright status</c>).</summary>
public static class JourneyStatus
{
    public static IReadOnlyList<JourneyStage> For(HubwrightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var store = session.Store;
        var latestProposal = store.IntranetProposals().FirstOrDefault();
        ImplementationPlan? build = null;
        if (latestProposal is not null && Path.Combine(latestProposal, Implementations.FileName) is var path && File.Exists(path))
        {
            build = ImplementationPlan.FromJson(File.ReadAllText(path));
        }

        ImplementationPlan? baseline = null;
        var security = Path.Combine(store.Directory, "security");
        if (Directory.Exists(security)
            && Directory.GetFiles(security, Implementations.FileName, SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}restore{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() is { } latest)
        {
            baseline = ImplementationPlan.FromJson(File.ReadAllText(latest));
        }

        var connected = session.Config.Tenant.Name is { Length: > 0 } name && string.Equals(name, store.Tenant, StringComparison.OrdinalIgnoreCase) ? name : null;
        return IntranetJourney.Stages(new JourneyInputs
        {
            AppRegistered = !string.IsNullOrWhiteSpace(session.Config.Tenant.ClientId),
            ConnectedTenant = connected,
            Tenant = store.Tenant,
            Snapshot = session.Workspace.Snapshot,
            Assessment = session.Workspace.Assessment,
            SnapshotCount = store.Snapshots().Count,
            HasPlan = latestProposal is not null || store.Blueprints().Count > 0,
            BuildSteps = build?.Steps.Count ?? 0,
            BuildFinished = build?.Steps.Count(s => s.IsFinished) ?? 0,
            BaselineSteps = baseline?.Steps.Count ?? 0,
            BaselineFinished = baseline?.Steps.Count(s => s.IsFinished) ?? 0,
        });
    }
}
