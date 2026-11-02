using Hubwright.Implementation;
using Hubwright.Proposals;
using Hubwright.Rules;

namespace Hubwright.Hints;

/// <summary>
/// The explanation behind a suggestion or a step: what it is, why it matters, how to do it, and an
/// example. Hints live in skills (<c>hints.yaml</c>); the "why" is backed by the rules they name,
/// whose Microsoft quotes are shown as the evidence. A hint may quote a page itself (source + quote).
/// </summary>
public sealed class Hint
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string What { get; set; } = "";

    public string Why { get; set; } = "";

    public List<string> How { get; set; } = [];

    public string? Example { get; set; }

    /// <summary>Rules whose quotes are the evidence for <see cref="Why"/>.</summary>
    public List<string> Rules { get; set; } = [];

    public string? Source { get; set; }

    public string? Quote { get; set; }

    public DateOnly? Reviewed { get; set; }

    public string? SkillId { get; set; }
}

/// <summary>A quote that backs a hint, with where it comes from.</summary>
public sealed record HintEvidence(string Quote, string Source, string Label);

/// <summary>Finds the hint for a suggestion, a site action or a step, and the evidence behind it.</summary>
public sealed class HintBook(IReadOnlyDictionary<string, Hint> hints, IReadOnlyDictionary<string, RuleDefinition> rules)
{
    public static readonly HintBook Empty = new(new Dictionary<string, Hint>(), new Dictionary<string, RuleDefinition>());

    public IReadOnlyDictionary<string, Hint> All => hints;

    public Hint? Find(string? id) => id is not null && hints.TryGetValue(id, out var hint) ? hint : null;

    /// <summary>The hint for a site action in a proposal.</summary>
    public Hint? For(SiteAction action) => Find(ActionHint(action));

    /// <summary>The hint for an implementation step: its own, or one made from the first rule it answers.</summary>
    public Hint? For(ImplementationStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return Find(step.Hint) ?? step.Rules.Select(FromRule).FirstOrDefault(h => h is not null);
    }

    /// <summary>The hint for a roadmap item: its own, or one made from its rule.</summary>
    public Hint? For(ProposalItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Find(item.Hint) ?? item.Rules.Select(FromRule).FirstOrDefault(h => h is not null);
    }

    /// <summary>A rule as a hint: its title, the fix, and its quote as the why.</summary>
    public Hint? FromRule(string ruleId)
    {
        if (!rules.TryGetValue(ruleId, out var rule))
        {
            return null;
        }

        return new Hint
        {
            Id = "rule:" + rule.Id,
            Title = rule.Title,
            What = rule.Title + ".",
            Why = rule.Basis == Basis.Documented ? "Microsoft's guidance, quoted below." : "A Hubwright heuristic: a judgment call with a threshold you can tune in overrides.yaml.",
            How = rule.Fix is null ? [] : [rule.Fix],
            Rules = [rule.Id],
            SkillId = rule.SkillId,
        };
    }

    /// <summary>The quotes that back a hint: its own, then its rules'.</summary>
    public IReadOnlyList<HintEvidence> Evidence(Hint hint)
    {
        ArgumentNullException.ThrowIfNull(hint);
        var list = new List<HintEvidence>();
        if (hint.Quote is not null && hint.Source is not null)
        {
            list.Add(new HintEvidence(hint.Quote, hint.Source, "Microsoft"));
        }

        foreach (var id in hint.Rules)
        {
            if (rules.TryGetValue(id, out var rule))
            {
                list.Add(rule.Basis == Basis.Documented && rule.Quote is not null && rule.Source is not null
                    ? new HintEvidence(rule.Quote, rule.Source, rule.Id)
                    : new HintEvidence($"Heuristic: {rule.Title}.", rule.Source ?? "", rule.Id));
            }
        }

        return list;
    }

    public static string? ActionHint(SiteAction action) => action switch
    {
        SiteAction.RenameHub => "rename-hub",
        SiteAction.BecomeHomeSite => "home-site",
        SiteAction.AssociateWithHub => "associate-with-hub",
        SiteAction.StopBeingHub => "unregister-hub",
        SiteAction.KeepOutOfHubs => "keep-extranet-out",
        SiteAction.Modernize => "modernize",
        SiteAction.FlattenSubsites => "flatten-subsites",
        SiteAction.Retire => "retire-site",
        SiteAction.Archive => "archive-site",
        SiteAction.ReviewWithOwner => "review-inactive",
        SiteAction.AddOwners => "add-owners",
        SiteAction.ReviewPermissions => "review-permissions",
        SiteAction.AddSensitivityLabel => "sensitivity-labels",
        SiteAction.ChooseHub => "choose-hub",
        SiteAction.CheckUnread => "unread-site",
        _ => null,
    };
}
