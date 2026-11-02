using Hubwright.Runtime;

namespace Hubwright.Config;

/// <summary>
/// Refuses the next model call once a spending ceiling is reached, from what the run logs
/// (<c>agent-sessions.jsonl</c>) have recorded. It checks before a call, so a single call can
/// overshoot by its own cost: the cost of a call is only known once it returns.
/// </summary>
public sealed class BudgetGuard(BudgetOptions options, string runsRoot)
{
    public bool Enabled => options.MaxRunUsd is not null || options.MaxDailyUsd is not null;

    public static decimal RunSpend(string runDirectory) => AgentLog.Read(runDirectory).Sum(e => e.CostUsd ?? 0m);

    /// <summary>What every run has spent today (UTC), in USD.</summary>
    public decimal DailySpend(DateTimeOffset? now = null)
    {
        var today = (now ?? DateTimeOffset.UtcNow).UtcDateTime.Date;
        if (!Directory.Exists(runsRoot))
        {
            return 0m;
        }

        return Directory.EnumerateDirectories(runsRoot)
            .SelectMany(directory => AgentLog.Read(directory))
            .Where(e => e.StartedAt.UtcDateTime.Date == today)
            .Sum(e => e.CostUsd ?? 0m);
    }

    /// <summary>Throws <see cref="BudgetExceededException"/> when a ceiling has been reached.</summary>
    public void EnsureWithinBudget(string? runDirectory, string what)
    {
        if (Enabled)
        {
            var today = DateTimeOffset.UtcNow.UtcDateTime.Date;
            var runCalls = options.MaxRunUsd is not null && runDirectory is not null
                ? AgentLog.Read(runDirectory, strict: true) : [];
            var dailyCalls = options.MaxDailyUsd is not null && Directory.Exists(runsRoot)
                ? Directory.EnumerateDirectories(runsRoot).SelectMany(directory => AgentLog.Read(directory, strict: true))
                    .Where(e => e.StartedAt.UtcDateTime.Date == today).ToArray() : [];
            var unknown = runCalls.Concat(dailyCalls).Where(e => e.CostUsd is null).ToArray();
            if (unknown.Length > 0)
            {
                throw new BudgetExceededException($"Cannot enforce a dollar budget before {what}: a prior or in-progress call has unknown cost. Wait for active calls, supply model pricing/usage, or explicitly disable dollar caps for an unmetered subscription; tool and runtime limits still apply.", callInFlight: unknown.All(e => e.EndedAt is null));
            }
        }

        if (options.MaxRunUsd is { } maxRun && runDirectory is not null && RunSpend(runDirectory) is var run && run >= maxRun)
        {
            throw new BudgetExceededException($"run budget reached before {what}: spent ${run:0.00} of ${maxRun:0.00} (ai.budget.maxRunUsd)");
        }

        if (options.MaxDailyUsd is { } maxDaily && DailySpend() is var daily && daily >= maxDaily)
        {
            throw new BudgetExceededException($"daily budget reached before {what}: spent ${daily:0.00} of ${maxDaily:0.00} today (ai.budget.maxDailyUsd)");
        }
    }
}

public sealed class BudgetExceededException(string message, bool callInFlight = false) : Exception(message)
{
    /// <summary>
    /// Refused only because another call has not reported its cost yet (it may still be running):
    /// the same call can succeed once that one finishes. A spent budget is <c>false</c>.
    /// </summary>
    public bool CallInFlight => callInFlight;
}
