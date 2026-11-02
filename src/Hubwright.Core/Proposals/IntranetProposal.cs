using System.Text.Json;
using Hubwright.Blueprints;
using Hubwright.Designs;
using Hubwright.Rules;
using Hubwright.Snapshot;

namespace Hubwright.Proposals;

/// <summary>How far the proposal moves away from what the tenant has today.</summary>
public enum ProposalApproach
{
    /// <summary>Keep the hubs and sites people know; fix risks, associate strays, modernize.</summary>
    KeepWhatWorks,

    /// <summary>Keep what fits the guidance, reshape what doesn't (hub depth, naming, regions), archive long-dead sites.</summary>
    Balanced,

    /// <summary>Design the target first and move everything into it; archive anything inactive past the policy.</summary>
    Restructure,
}

/// <summary>Everything the proposal questionnaire collects. Seeded from the scan, then confirmed by the admin.</summary>
public sealed class ProposalAnswers
{
    public IntranetAnswers Intranet { get; set; } = new();

    public ProposalApproach Approach { get; set; } = ProposalApproach.Balanced;

    /// <summary>Also suggest look book PnP packages, not only Microsoft's built-in site templates.</summary>
    public bool UsePackages { get; set; } = true;

    /// <summary>Answers to skill questions (questions.yaml), by question id.</summary>
    public Dictionary<string, string> Decisions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static ProposalAnswers FromJson(string json)
        => JsonSerializer.Deserialize<ProposalAnswers>(json, TenantSnapshot.Json) ?? new ProposalAnswers();

    public string ToJson() => JsonSerializer.Serialize(this, TenantSnapshot.Json);
}

/// <summary>What happens to one site.</summary>
public enum SiteAction
{
    Keep,
    RenameHub,
    BecomeHomeSite,
    AssociateWithHub,
    StopBeingHub,
    KeepOutOfHubs,
    Modernize,
    FlattenSubsites,
    Retire,
    Archive,
    ReviewWithOwner,
    AddOwners,
    ReviewPermissions,
    AddSensitivityLabel,
    ChooseHub,

    /// <summary>The scan could not read the site (the person who scanned is not a member): someone with access checks it.</summary>
    CheckUnread,
}

/// <summary>An existing site in the proposal: where it goes and what to do with it.</summary>
public sealed class SitePlan
{
    public string Url { get; set; } = "";

    public string Title { get; set; } = "";

    public SiteKind Kind { get; set; }

    public bool IsHub { get; set; }

    public string? CurrentHub { get; set; }

    public string? TargetHub { get; set; }

    /// <summary>One word for the table: keep, move, modernize, retire, archive, review.</summary>
    public string Disposition { get; set; } = "keep";

    public List<SiteAction> Actions { get; set; } = [];

    public List<string> Reasons { get; set; } = [];

    /// <summary>The rules behind the actions, for the citations.</summary>
    public List<string> Rules { get; set; } = [];

    public int? DaysInactive { get; set; }
}

/// <summary>A site the proposal creates, with the ready-made template that fits it best.</summary>
public sealed class NewSitePlan
{
    public string Title { get; set; } = "";

    public string Url { get; set; } = "";

    public string? Hub { get; set; }

    public string Purpose { get; set; } = "";

    public SiteKind Kind { get; set; }

    public string? TemplateId { get; set; }

    public string? TemplateName { get; set; }

    public string? HowToApply { get; set; }

    /// <summary>Other templates that would also fit.</summary>
    public List<string> Alternatives { get; set; } = [];
}

public sealed class ProposalItem
{
    public string Text { get; set; } = "";

    public List<string> Rules { get; set; } = [];

    /// <summary>The hint (hints.yaml id) that explains it; rule items explain themselves through their rule.</summary>
    public string? Hint { get; set; }
}

public sealed class ProposalPhase
{
    public string Name { get; set; } = "";

    public string When { get; set; } = "";

    public string Goal { get; set; } = "";

    public List<ProposalItem> Items { get; set; } = [];
}

public sealed class ProposalDecision
{
    public string Id { get; set; } = "";

    public string SkillId { get; set; } = "";

    public string Question { get; set; } = "";

    public string? Answer { get; set; }

    public string? Why { get; set; }
}

public sealed class ProposalIssue
{
    public string RuleId { get; set; } = "";

    public string Title { get; set; } = "";

    public string SkillId { get; set; } = "";

    public Severity Severity { get; set; }

    public Basis Basis { get; set; }

    public int Count { get; set; }

    public string? Fix { get; set; }

    public string? Source { get; set; }

    public string? Quote { get; set; }
}

public sealed class CategoryChange
{
    public string Category { get; set; } = "";

    public double? Before { get; set; }

    public double? After { get; set; }
}

public sealed class ProposalCitation
{
    public string Url { get; set; } = "";

    public List<string> Quotes { get; set; } = [];

    public List<string> Rules { get; set; } = [];
}

/// <summary>
/// An intranet proposal for a scanned tenant: what it has today, the target structure (hubs, sites,
/// navigation, theme, policies) with every existing site placed in it, new sites with ready-made
/// SharePoint templates, a phased roadmap and the decisions the admin made. Every number comes from
/// the rules; the optional narrative is the AI's.
/// </summary>
public sealed class IntranetProposal
{
    public string Name { get; set; } = "";

    public string Tenant { get; set; } = "";

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset SnapshotTaken { get; set; }

    public ProposalAnswers Answers { get; set; } = new();

    /// <summary>What the scan told the questionnaire, so the admin can see where each default came from.</summary>
    public List<string> Observations { get; set; } = [];

    public double? ScoreBefore { get; set; }

    /// <summary>The target design checked against the same rules, as if it were built.</summary>
    public double? ScoreAfter { get; set; }

    public List<CategoryChange> Categories { get; set; } = [];

    public int SiteCount { get; set; }

    public int HubCount { get; set; }

    public int FindingCount { get; set; }

    public int NotAssessed { get; set; }

    /// <summary>Sites the scan could not read (not a member): planned from the usage report only.</summary>
    public int UnreadSites { get; set; }

    public List<ProposalIssue> TopIssues { get; set; } = [];

    public Blueprint Blueprint { get; set; } = new();

    public List<SitePlan> Sites { get; set; } = [];

    public List<NewSitePlan> NewSites { get; set; } = [];

    /// <summary>The proposed design of every site (purpose, pattern, home page sections), from the site-design skill.</summary>
    public List<SiteDesign> Designs { get; set; } = [];

    public List<ProposalPhase> Phases { get; set; } = [];

    public List<ProposalDecision> Decisions { get; set; } = [];

    public List<ProposalCitation> Citations { get; set; } = [];

    public List<string> ThemeNotes { get; set; } = [];

    /// <summary>The architect's summary, when AI was used. Never the source of a number.</summary>
    public string? Narrative { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, TenantSnapshot.Json);

    public static IntranetProposal FromJson(string json)
        => JsonSerializer.Deserialize<IntranetProposal>(json, TenantSnapshot.Json) ?? throw new InvalidDataException("The proposal file is empty.");
}
