using System.Diagnostics;
using Hubwright.Implementation;
using Hubwright.Journey;
using Hubwright.Monitoring;
using Hubwright.Scoring;
using Hubwright.Snapshot;
using Hubwright.Storage;

namespace Hubwright.Engine;

/// <summary>One scan's grade, for the trend across scans.</summary>
public sealed record TrendPoint(DateTimeOffset At, double? Overall, IReadOnlyDictionary<string, double?> Categories, int Findings);

/// <summary>
/// The Run stage: drift from the plan and the baseline, the score across scans, owner lists, a short
/// summary to share, and opt-in scheduled scans. Everything here reads; nothing writes to the tenant.
/// </summary>
public static class IntranetMonitor
{
    public static IReadOnlyList<DriftItem> Drift(HubwrightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = session.Workspace.Snapshot ?? throw new InvalidOperationException("No scan yet: run `hubwright scan` first.");
        var folder = session.Store.IntranetProposals().FirstOrDefault();
        var blueprint = folder is not null ? TenantStore.LoadIntranetProposal(folder).Blueprint : session.Workspace.Blueprint;
        var build = folder is not null && File.Exists(Path.Combine(folder, Implementations.FileName)) ? ImplementationPlan.FromJson(File.ReadAllText(Path.Combine(folder, Implementations.FileName))) : null;
        return Monitoring.Drift.Check(snapshot, blueprint, build, session.Skills.Baseline, LatestBaseline(session));
    }

    /// <summary>The baseline plan changed most recently (any profile), or null.</summary>
    public static ImplementationPlan? LatestBaseline(HubwrightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var security = Path.Combine(session.Store.Directory, "security");
        if (!Directory.Exists(security))
        {
            return null;
        }

        var latest = Directory.GetFiles(security, Implementations.FileName, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}restore{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return latest is null ? null : ImplementationPlan.FromJson(File.ReadAllText(latest));
    }

    /// <summary>The grade of every scan, oldest first (the last <paramref name="max"/>), assessed with today's rules.</summary>
    public static IReadOnlyList<TrendPoint> Trend(HubwrightSession session, int max = 24)
    {
        ArgumentNullException.ThrowIfNull(session);
        var points = new List<TrendPoint>();
        foreach (var path in session.Store.Snapshots().Take(max).Reverse())
        {
            var snapshot = TenantSnapshot.FromJson(File.ReadAllText(path));
            var a = session.Workspace.Assess(snapshot);
            points.Add(new TrendPoint(snapshot.CapturedAt, a.Overall, a.Categories.ToDictionary(c => c.Category, c => c.Score, StringComparer.OrdinalIgnoreCase), a.Findings.Count));
        }

        return points;
    }

    public static IReadOnlyList<OwnerReport.Row> Owners(HubwrightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var snapshot = session.Workspace.Snapshot ?? throw new InvalidOperationException("No scan yet: run `hubwright scan` first.");
        var minimum = session.Workspace.Blueprint?.Policy.MinimumOwners ?? 2;
        return OwnerReport.For(snapshot, minimum);
    }

    public static string Summary(HubwrightSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var assessment = session.Workspace.RequireAssessment();
        var stages = JourneyStatus.For(session).Select(s => (s.Title, s.State switch { StageState.Done => "✅", StageState.InProgress => "🟡", _ => "⚪" }, s.Status));
        return Monitoring.Summary.Markdown(session.Store.Tenant, assessment, stages, Drift(session));
    }

    /// <summary>The Windows scheduled task's name for a tenant's scans.</summary>
    public static string TaskName(string tenant) => $"Hubwright scan {TenantStore.Clean(tenant)}";

    /// <summary>
    /// Schedules (or removes) a weekly or daily read-only scan with Windows Task Scheduler. It uses
    /// the signed-in person's cached token and never prompts (<c>scan --no-prompt</c>); when that
    /// token can't be renewed silently, the scan stops and the audit log says so.
    /// </summary>
    public static async Task<string> ScheduleAsync(string tenant, string frequency, string executable, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executable);
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Scheduled scans use Windows Task Scheduler. Elsewhere, run `hubwright scan --no-prompt` from cron.");
        }

        var name = TaskName(tenant);
        string[] arguments = frequency.ToLowerInvariant() switch
        {
            "off" => ["/Delete", "/TN", name, "/F"],
            "daily" => ["/Create", "/TN", name, "/TR", $"\"{executable}\" scan --tenant {TenantStore.Clean(tenant)} --no-prompt", "/SC", "DAILY", "/ST", "07:00", "/F"],
            "weekly" => ["/Create", "/TN", name, "/TR", $"\"{executable}\" scan --tenant {TenantStore.Clean(tenant)} --no-prompt", "/SC", "WEEKLY", "/D", "MON", "/ST", "07:00", "/F"],
            "status" => ["/Query", "/TN", name],
            _ => throw new InvalidOperationException("Choose weekly, daily, off or status."),
        };
        var start = new ProcessStartInfo("schtasks.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start schtasks.exe.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            return frequency == "status" ? "Not scheduled." : throw new InvalidOperationException($"Task Scheduler refused: {error.Trim()}");
        }

        return frequency switch
        {
            "off" => "Scheduled scans are off.",
            "status" => output.Trim(),
            _ => $"Scans {frequency} at 07:00 (Monday for weekly) as you, read-only. If your sign-in can't be renewed silently, the scan stops and the audit log says so.",
        };
    }
}
