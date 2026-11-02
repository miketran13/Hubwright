using System.Text.Json;
using Hubwright.Blueprints;
using Hubwright.Proposals;
using Hubwright.Snapshot;
using Hubwright.Templates;

namespace Hubwright.Implementation;

/// <summary>Whether Hubwright does a step itself or walks the admin through it.</summary>
public enum StepKind
{
    /// <summary>Hubwright calls a documented, allow-listed API.</summary>
    Automatic,

    /// <summary>The admin does it in the in-app browser; Hubwright opens the page and lists what to enter.</summary>
    Guided,
}

/// <summary>How much a step can disturb what people use today.</summary>
public enum StepRisk
{
    /// <summary>Creates something new; nothing existing changes.</summary>
    Adds,

    /// <summary>Changes something that exists (a setting, a hub name, a site's hub).</summary>
    Changes,

    /// <summary>Hard to undo or visible to everyone (archive, retire, unregister). Never automatic.</summary>
    Caution,
}

public enum StepStatus { Pending, Running, Done, AlreadyDone, Skipped, Failed, Blocked }

/// <summary>What an automatic step calls. Each maps to one documented endpoint in TenantWriter.</summary>
public enum StepOperation
{
    None,
    AddOrUpdateTheme,
    CreateSite,
    RegisterHub,
    RenameHub,
    AssociateWithHub,
    DisassociateFromHub,
    ApplyTheme,
    SetSharing,

    /// <summary>Security baseline: one documented property of Graph sharepointSettings.</summary>
    SetTenantSetting,

    // PnP PowerShell steps (PnpScripts): SharePoint documents no REST call Hubwright may use for these.
    SetHomeSite,
    BuildNavigation,
    ApplySiteTemplate,
    ProvisionPackage,
    SetSiteHeader,
    SetSiteLogo,

    /// <summary>Security baseline: one allow-listed Set-PnPTenant parameter.</summary>
    SetTenantPolicy,

    /// <summary>Launch: make a site an organization news site (Add-PnPOrgNewsSite).</summary>
    AddOrgNewsSite,

    /// <summary>Launch: a draft news post with sample text (Add-PnPPage, never published).</summary>
    AddStarterPage,

    /// <summary>Site design: a draft page with the design's sections as web parts (Add-PnPPage, never published).</summary>
    AddDesignPage,

    /// <summary>
    /// Site design, only when the person chose Publish: publish the drafted page (Set-PnPPage -Publish)
    /// and make it the home page (Set-PnPHomePage), after recording the previous home page.
    /// </summary>
    PublishDesignPage,
}

/// <summary>One step of an implementation: what, where, why, and what happened.</summary>
public sealed class ImplementationStep
{
    public string Id { get; set; } = "";

    public string Phase { get; set; } = "";

    public string Title { get; set; } = "";

    public string Detail { get; set; } = "";

    public StepKind Kind { get; set; }

    public StepRisk Risk { get; set; }

    public StepOperation Operation { get; set; }

    /// <summary>The site the step acts on, when there is one.</summary>
    public string? Target { get; set; }

    /// <summary>Inputs: a value of the form <c>{step-id.output}</c> is filled from an earlier step's outputs at run time.</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);

    public List<string> DependsOn { get; set; } = [];

    /// <summary>Guided steps: the page to open in the in-app browser.</summary>
    public string? GuideUrl { get; set; }

    /// <summary>Guided steps: what to do there, one line each (values to paste included).</summary>
    public List<string> Instructions { get; set; } = [];

    /// <summary>The documentation the automatic call follows.</summary>
    public string? Source { get; set; }

    public List<string> Rules { get; set; } = [];

    /// <summary>The hint (hints.yaml id) that explains what, why, how and an example.</summary>
    public string? Hint { get; set; }

    public StepStatus Status { get; set; }

    public string? Result { get; set; }

    /// <summary>Values later steps can use, such as a new hub's id.</summary>
    public Dictionary<string, string> Outputs { get; set; } = new(StringComparer.Ordinal);

    public DateTimeOffset? Finished { get; set; }

    public bool IsFinished => Status is StepStatus.Done or StepStatus.AlreadyDone or StepStatus.Skipped;
}

/// <summary>A proposal turned into ordered steps. Saved next to the proposal as implementation.json and updated as steps run.</summary>
public sealed class ImplementationPlan
{
    public string Proposal { get; set; } = "";

    public string Tenant { get; set; } = "";

    public string RootUrl { get; set; } = "";

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public List<ImplementationStep> Steps { get; set; } = [];

    public ImplementationStep? Find(string id) => Steps.FirstOrDefault(s => s.Id == id);

    /// <summary>The steps, plus every unfinished step they depend on, in plan order.</summary>
    public IReadOnlyList<ImplementationStep> WithDependencies(IEnumerable<string> ids)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        void Add(string id)
        {
            if (!wanted.Add(id) || Find(id) is not { } step)
            {
                return;
            }

            foreach (var dependency in step.DependsOn.Where(d => Find(d) is { IsFinished: false }))
            {
                Add(dependency);
            }
        }

        foreach (var id in ids)
        {
            Add(id);
        }

        return [.. Steps.Where(s => wanted.Contains(s.Id))];
    }

    public string ToJson() => JsonSerializer.Serialize(this, TenantSnapshot.Json);

    public static ImplementationPlan FromJson(string json)
        => JsonSerializer.Deserialize<ImplementationPlan>(json, TenantSnapshot.Json) ?? throw new InvalidDataException("The implementation file is empty.");
}
