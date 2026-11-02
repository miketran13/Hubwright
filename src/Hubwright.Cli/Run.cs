using Hubwright.Engine;
using Hubwright.Monitoring;

namespace Hubwright.Cli;

/// <summary>Stage 6, Run: drift, the score across scans, owner lists, a summary to share, and scheduled scans.</summary>
internal static partial class Commands
{
    internal static int DriftCommand()
    {
        var session = Session();
        var items = IntranetMonitor.Drift(session);
        if (Csv(CsvExport.Drift(items)))
        {
            return 0;
        }

        foreach (var group in items.GroupBy(i => i.Area))
        {
            Console.WriteLine($"\n{group.Key}");
            foreach (var d in group)
            {
                var mark = d.State switch { DriftState.InPlace => "✓", DriftState.Drifted => "✗ drifted", DriftState.NotBuilt => "· to build", _ => "? not read" };
                Console.WriteLine($"  {mark,-11} {d.Title,-48} expected {d.Expected} · now {d.Actual ?? "not read"}");
            }
        }

        var drifted = items.Count(i => i.State == DriftState.Drifted);
        Console.WriteLine($"\n{drifted} drifted, {items.Count(i => i.State == DriftState.NotBuilt)} not built yet, {items.Count(i => i.State == DriftState.NotRead)} not read. Compared with the scan of {session.Workspace.Snapshot!.CapturedAt:yyyy-MM-dd}; scan again for today.");
        return drifted > 0 ? 3 : 0;
    }

    internal static int Trend()
    {
        var points = IntranetMonitor.Trend(Session());
        if (points.Count == 0)
        {
            Console.WriteLine("No scans yet.");
            return 0;
        }

        var categories = points.SelectMany(p => p.Categories.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        Console.WriteLine($"{"Scan",-17} {"Overall",7} {"Findings",8}  {string.Join(" ", categories.Select(c => c.Length > 10 ? c[..10] : c).Select(c => $"{c,10}"))}");
        foreach (var p in points)
        {
            Console.WriteLine($"{p.At.ToLocalTime():yyyy-MM-dd HH:mm} {Score(p.Overall),7} {p.Findings,8}  {string.Join(" ", categories.Select(c => $"{Score(p.Categories.GetValueOrDefault(c)),10}"))}");
        }

        return 0;
    }

    internal static int Owners()
    {
        var rows = IntranetMonitor.Owners(Session());
        if (Csv(CsvExport.Owners(rows)))
        {
            return 0;
        }

        foreach (var r in rows)
        {
            Console.WriteLine($"{r.Status,-12} {r.Title,-40} {r.Url}");
        }

        Console.WriteLine($"\n{rows.Count} site(s) below the minimum number of owners. `hubwright owners --csv owners.csv` gives a list to send to each site's members for confirmation.");
        return 0;
    }

    internal static int SummaryCommand()
    {
        var text = IntranetMonitor.Summary(Session());
        if (cli.Option("out") is { } path)
        {
            File.WriteAllText(path, text);
            Console.WriteLine($"Written to {Path.GetFullPath(path)}: paste it into Teams or an email.");
            return 0;
        }

        Console.WriteLine(text);
        return 0;
    }

    internal static async Task<int> ScheduleAsync()
    {
        var session = Session();
        var exe = self ?? throw new InvalidOperationException("Can't tell where hubwright is installed.");
        Console.WriteLine(await IntranetMonitor.ScheduleAsync(session.Store.Tenant, cli.At(1)?.ToLowerInvariant() ?? "status", exe));
        session.Audit.Append("schedule", $"{session.Store.Tenant} {cli.At(1) ?? "status"}");
        return 0;
    }

    /// <summary>Writes CSV when --csv was given; true when it did.</summary>
    private static bool Csv(string csv)
    {
        if (cli.Option("csv") is not { } path)
        {
            return false;
        }

        File.WriteAllText(path, csv);
        Console.WriteLine($"Written to {Path.GetFullPath(path)} (opens in Excel).");
        return true;
    }
}
