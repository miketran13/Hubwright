using Hubwright.Implementation;
using Hubwright.Security;

namespace Hubwright.Engine;

/// <summary>
/// The Secure stage: one baseline plan per profile under <c>tenants/&lt;name&gt;/security/&lt;profile&gt;/</c>,
/// run, checked and restored by the same runner and write gates the Build stage uses.
/// </summary>
public static class SecurityBaselines
{
    public static string Folder(HubwrightSession session, BaselineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Directory.CreateDirectory(Path.Combine(session.Store.Directory, "security", profile.ToString().ToLowerInvariant())).FullName;
    }

    public static string RestoreFolder(HubwrightSession session, BaselineProfile profile)
        => Directory.CreateDirectory(Path.Combine(Folder(session, profile), "restore")).FullName;

    /// <summary>The settings in this profile compared with the latest scan.</summary>
    public static IReadOnlyList<BaselineItem> Compare(HubwrightSession session, BaselineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = session.Workspace.Snapshot ?? throw new InvalidOperationException("No scan yet: run `hubwright scan` (or load a sample) first.");
        return SecurityBaseline.Compare(snapshot.Settings, session.Skills.Baseline, profile);
    }

    /// <summary>The saved plan for this profile (with its progress), or a new one from the latest scan.</summary>
    public static ImplementationPlan For(HubwrightSession session, BaselineProfile profile, bool rebuild = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        var folder = Folder(session, profile);
        var path = Path.Combine(folder, Implementations.FileName);
        if (File.Exists(path) && !rebuild)
        {
            return ImplementationPlan.FromJson(File.ReadAllText(path));
        }

        var snapshot = session.Workspace.Snapshot ?? throw new InvalidOperationException("No scan yet: run `hubwright scan` (or load a sample) first.");
        var root = session.Config.Tenant.Name is { } name && string.Equals(name, session.Store.Tenant, StringComparison.OrdinalIgnoreCase)
            ? session.Config.Tenant.RootUrl
            : $"https://{session.Store.Tenant}.sharepoint.com";
        var plan = SecurityBaseline.Plan(snapshot, session.Skills.Baseline, profile, root);
        if (File.Exists(path))
        {
            var old = ImplementationPlan.FromJson(File.ReadAllText(path));
            Implementations.Carry(old, plan);
            foreach (var step in plan.Steps)
            {
                // Keep what a run recorded, so Restore still knows the earlier values.
                if (old.Find(step.Id) is { } before && before.Outputs.TryGetValue("previous", out var previous))
                {
                    step.Outputs.TryAdd("previous", previous);
                }
            }
        }

        Implementations.Save(plan, folder);
        session.Audit.Append("secure.plan", $"{session.Store.Tenant} · {profile} · {plan.Steps.Count} setting(s)");
        return plan;
    }

    /// <summary>A restore plan for what Hubwright changed under this profile, saved next to it.</summary>
    public static ImplementationPlan Restore(HubwrightSession session, BaselineProfile profile)
    {
        var applied = For(session, profile);
        var plan = SecurityBaseline.Restore(applied);
        if (plan.Steps.Count == 0)
        {
            throw new InvalidOperationException("Nothing to restore: Hubwright hasn't changed any setting under this profile (guided steps are undone by hand).");
        }

        Implementations.Save(plan, RestoreFolder(session, profile));
        return plan;
    }

    /// <summary>Whether this tenant workspace is a real tenant (not a sample, import or design), so writes make sense.</summary>
    public static bool IsLive(HubwrightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.Workspace.Snapshot?.Source == Snapshot.SnapshotSource.Scan
            && string.Equals(session.Config.Tenant.Name, session.Store.Tenant, StringComparison.OrdinalIgnoreCase);
    }
}
