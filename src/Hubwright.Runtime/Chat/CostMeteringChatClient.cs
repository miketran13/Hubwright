using Microsoft.Extensions.AI;
using Hubwright.Config;
using Hubwright.Runtime;

namespace Hubwright.Chat;

/// <summary>
/// Stamps a dollar cost onto every response's <see cref="UsageDetails"/>, so the pipeline can
/// ledger and cap spend the same way whichever backend answered.
///
/// A backend that already knows its cost (the claude CLI) sets it itself and is left alone;
/// everything else is priced from its token counts.
/// </summary>
public sealed class CostMeteringChatClient(IChatClient inner, ModelPricing pricing, BudgetOptions? budgetOptions = null) : DelegatingChatClient(inner)
{
    /// <summary>
    /// <see cref="UsageDetails.AdditionalCounts"/> holds integers only, so cost travels in
    /// millionths of a dollar.
    /// </summary>
    public const string CostMicrosKey = "hubwright.cost_usd_micros";

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        AgentCall? call = null;
        if (budgetOptions is not null && AgentLog.RunDirectory is { } directory)
        {
            new BudgetGuard(budgetOptions, Path.GetDirectoryName(directory)!).EnsureWithinBudget(directory, "model response");
            call = AgentLog.Begin("api", options?.ModelId, "", transcript: false);
        }

        ChatResponse response;
        try
        {
            response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            call?.End(false, "", SensitiveText.Redact(ex.Message));
            throw;
        }

        if (response.Usage is { } usage && ReadCost(usage) is null)
        {
            var model = response.ModelId
                ?? options?.ModelId
                ?? this.GetService<ChatClientMetadata>()?.DefaultModelId;

            if (usage.InputTokenCount is >= 0 && usage.OutputTokenCount is >= 0
                && pricing.Estimate(model, usage.InputTokenCount.Value, usage.OutputTokenCount.Value) is { } cost)
            {
                WriteCost(usage, cost);
            }
        }

        call?.End(true, "", inputTokens: response.Usage?.InputTokenCount, outputTokens: response.Usage?.OutputTokenCount, costUsd: ReadCost(response.Usage));
        return response;
    }

    public static decimal? ReadCost(UsageDetails? usage)
        => usage?.AdditionalCounts?.TryGetValue(CostMicrosKey, out var micros) == true
            ? micros / 1_000_000m
            : null;

    public static void WriteCost(UsageDetails usage, decimal costUsd)
    {
        usage.AdditionalCounts ??= [];
        usage.AdditionalCounts[CostMicrosKey] = (long)Math.Round(costUsd * 1_000_000m);
    }
}
