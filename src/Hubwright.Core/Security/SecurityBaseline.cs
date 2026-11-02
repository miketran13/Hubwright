using System.Globalization;
using Hubwright.Implementation;
using Hubwright.Rules;
using Hubwright.Snapshot;

namespace Hubwright.Security;

/// <summary>How strict the baseline is. Each setting lists a target per profile, or none.</summary>
public enum BaselineProfile
{
    /// <summary>Guests who sign in; organization links by default; legacy authentication off.</summary>
    Standard,

    /// <summary>Existing guests only; specific-people links; idle sign-out; limited access from unmanaged devices.</summary>
    Strict,

    /// <summary>No external sharing; unmanaged devices blocked; labels and DLP required.</summary>
    Regulated,
}

/// <summary>How a baseline setting is put in place.</summary>
public enum BaselineApply
{
    /// <summary>Graph <c>PATCH /admin/sharepoint/settings</c>, one documented property.</summary>
    Graph,

    /// <summary>PnP PowerShell <c>Set-PnPTenant</c> with one allow-listed parameter.</summary>
    Pnp,

    /// <summary>The admin does it in the in-app browser (Purview, Entra, SharePoint Advanced Management).</summary>
    Guided,
}

/// <summary>
/// One setting of the security baseline (skills/security-baseline/baseline.yaml): what it sets, the
/// value per profile, how it is applied, and the Microsoft page it follows, quoted.
/// </summary>
public sealed class BaselineSetting
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>sharing, links, guests, sessions, devices, labels, sam, lifecycle.</summary>
    public string Area { get; set; } = "";

    public BaselineApply Apply { get; set; }

    /// <summary>The Graph property or the Set-PnPTenant parameter.</summary>
    public string? Property { get; set; }

    /// <summary>string, int, bool or json (Graph complex values).</summary>
    public string ValueType { get; set; } = "string";

    /// <summary>The target value per profile (standard, strict, regulated). A profile that isn't listed doesn't include this setting.</summary>
    public Dictionary<string, string> Targets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The snapshot setting it is read from (TenantSettings property, camelCase); null when the scan doesn't read it.</summary>
    public string? Fact { get; set; }

    /// <summary>Why this matters, in one sentence.</summary>
    public string Why { get; set; } = "";

    public Basis Basis { get; set; } = Basis.Documented;

    public string? Source { get; set; }

    public string? Quote { get; set; }

    public DateOnly? Reviewed { get; set; }

    /// <summary>For Hubwright's own choices (a number of days): what the value is based on.</summary>
    public string? Note { get; set; }

    /// <summary>Guided steps: the page to open ({admin} is the SharePoint admin center).</summary>
    public string? GuideUrl { get; set; }

    public List<string> Instructions { get; set; } = [];

    public List<string> Rules { get; set; } = [];

    public string? Hint { get; set; }

    public string SkillId { get; set; } = "";

    public string? Target(BaselineProfile profile) => Targets.GetValueOrDefault(profile.ToString());
}

public enum BaselineState
{
    /// <summary>The scan read the value and it already is the target.</summary>
    Matches,

    /// <summary>The scan read a different value.</summary>
    Differs,

    /// <summary>The scan couldn't read it. Not a pass: it is read again before anything changes.</summary>
    NotRead,
}

/// <summary>One setting compared with the tenant as the last scan saw it.</summary>
public sealed record BaselineItem(BaselineSetting Setting, string? Current, string Target, BaselineState State);

/// <summary>
/// Compares a snapshot with a baseline profile and turns the differences into steps for the same
/// runner the Build stage uses: documented Graph writes, allow-listed PnP parameters, and guided
/// steps for everything else. A restore plan puts back the values recorded before each change.
/// </summary>
public static class SecurityBaseline
{
    public const string GraphSettingsDoc = "https://learn.microsoft.com/en-us/graph/api/sharepointsettings-update";
    public const string SetPnPTenantDoc = "https://pnp.github.io/powershell/cmdlets/Set-PnPTenant.html";

    /// <summary>
    /// The only Graph sharepointSettings properties Hubwright changes (TenantWriter enforces it): the
    /// sharing level and the baseline's documented, updatable settings.
    /// </summary>
    public static readonly IReadOnlyList<string> GraphProperties =
    [
        "sharingCapability", "isResharingByExternalUsersEnabled", "isRequireAcceptingUserToMatchInvitedUserEnabled",
        "idleSessionSignOut", "isLegacyAuthProtocolsEnabled",
    ];

    /// <summary>The settings in this profile, compared with the snapshot.</summary>
    public static IReadOnlyList<BaselineItem> Compare(TenantSettings settings, IEnumerable<BaselineSetting> baseline, BaselineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(baseline);
        var items = new List<BaselineItem>();
        foreach (var setting in baseline)
        {
            if (setting.Target(profile) is not { } target)
            {
                continue;
            }

            var current = Current(settings, setting.Fact);
            var state = current is null ? BaselineState.NotRead : Same(current, target, setting) ? BaselineState.Matches : BaselineState.Differs;
            items.Add(new BaselineItem(setting, current, target, state));
        }

        return items;
    }

    /// <summary>The baseline as build steps. Settings the scan saw in place start as already done; Check confirms them live.</summary>
    public static ImplementationPlan Plan(TenantSnapshot snapshot, IEnumerable<BaselineSetting> baseline, BaselineProfile profile, string rootUrl)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var admin = rootUrl.Replace(".sharepoint.com", "-admin.sharepoint.com", StringComparison.OrdinalIgnoreCase);
        var plan = new ImplementationPlan { Proposal = PlanName(profile), Tenant = snapshot.Tenant, RootUrl = rootUrl };
        foreach (var item in Compare(snapshot.Settings, baseline, profile))
        {
            var s = item.Setting;
            var step = new ImplementationStep
            {
                Id = "baseline-" + s.Id,
                Phase = Area(s.Area),
                Title = $"{s.Title}: {Human(item.Target)}",
                Detail = $"Today: {(item.Current is null ? "not read in the scan (read again before anything changes)" : Human(item.Current))}. {s.Why}",
                Source = s.Apply switch { BaselineApply.Graph => GraphSettingsDoc, BaselineApply.Pnp => SetPnPTenantDoc, _ => s.Source },
                Rules = [.. s.Rules],
                Hint = s.Hint,
                Risk = s.Apply == BaselineApply.Guided && s.Area == "devices" ? StepRisk.Caution : StepRisk.Changes,
            };
            switch (s.Apply)
            {
                case BaselineApply.Graph:
                    step.Kind = StepKind.Automatic;
                    step.Operation = StepOperation.SetTenantSetting;
                    step.Parameters["property"] = s.Property ?? throw new InvalidOperationException($"Baseline setting {s.Id} has no property.");
                    step.Parameters["value"] = item.Target;
                    step.Parameters["type"] = s.ValueType;
                    break;
                case BaselineApply.Pnp:
                    step.Kind = StepKind.Automatic;
                    step.Operation = StepOperation.SetTenantPolicy;
                    step.Target = admin;
                    step.Parameters["parameter"] = s.Property ?? throw new InvalidOperationException($"Baseline setting {s.Id} has no parameter.");
                    step.Parameters["value"] = item.Target;
                    step.Parameters["type"] = s.ValueType;
                    step.GuideUrl = admin + "/_layouts/15/online/AdminHome.aspx#/sharing";
                    step.Instructions = [$"SharePoint admin center → Policies → Sharing: {s.Title} → {Human(item.Target)}.", $"Or in PowerShell: Set-PnPTenant -{s.Property} {item.Target}"];
                    break;
                default:
                    step.Kind = StepKind.Guided;
                    step.GuideUrl = s.GuideUrl?.Replace("{admin}", admin, StringComparison.Ordinal);
                    step.Instructions = [.. s.Instructions];
                    break;
            }

            if (item.State == BaselineState.Matches)
            {
                step.Status = StepStatus.AlreadyDone;
                step.Result = $"Already in place in the scan of {snapshot.CapturedAt:yyyy-MM-dd}.";
            }

            plan.Steps.Add(step);
        }

        // Guest expiry in days needs expiry switched on first.
        foreach (var days in plan.Steps.Where(s => s.Parameters.GetValueOrDefault("parameter") == "ExternalUserExpireInDays"))
        {
            days.DependsOn.AddRange(plan.Steps.Where(s => s.Parameters.GetValueOrDefault("parameter") == "ExternalUserExpirationRequired").Select(s => s.Id));
        }

        return plan;
    }

    /// <summary>
    /// The steps that put back what Hubwright changed: every automatic step it ran recorded the value
    /// it replaced. Guided steps and values nobody recorded are left out (do them by hand).
    /// </summary>
    public static ImplementationPlan Restore(ImplementationPlan applied)
    {
        ArgumentNullException.ThrowIfNull(applied);
        var plan = new ImplementationPlan { Proposal = applied.Proposal + "-restore", Tenant = applied.Tenant, RootUrl = applied.RootUrl };
        foreach (var step in applied.Steps.Where(s => s.Status == StepStatus.Done && s.Kind == StepKind.Automatic && s.Outputs.TryGetValue("previous", out var p) && p.Length > 0))
        {
            var back = new ImplementationStep
            {
                Id = "restore-" + step.Id["baseline-".Length..],
                Phase = "Restore",
                Title = $"Restore: {step.Title.Split(':')[0]} → {Human(step.Outputs["previous"])}",
                Detail = $"Hubwright changed this on {step.Finished:yyyy-MM-dd}; this puts back the value it replaced.",
                Kind = StepKind.Automatic,
                Risk = StepRisk.Changes,
                Operation = step.Operation,
                Target = step.Target,
                Source = step.Source,
                Hint = "baseline-restore",
                Parameters = new Dictionary<string, string>(step.Parameters, StringComparer.Ordinal) { ["value"] = step.Outputs["previous"] },
            };
            plan.Steps.Add(back);
        }

        return plan;
    }

    public static string PlanName(BaselineProfile profile) => "security-baseline-" + profile.ToString().ToLowerInvariant();

    /// <summary>The snapshot's value in the apply domain (the value the Graph property or PnP parameter takes), or null when not read.</summary>
    public static string? Current(TenantSettings s, string? fact)
    {
        ArgumentNullException.ThrowIfNull(s);
        static string? B(bool? b) => b is null ? null : b.Value ? "true" : "false";
        static string? I(int? i) => i?.ToString(CultureInfo.InvariantCulture);
        return fact switch
        {
            "sharingLevel" => s.SharingLevel is { } l ? ImplementationPlanner.SharingCapability(l) : null,
            "defaultLinkType" => s.DefaultLinkType switch { DefaultLinkType.Anyone => "AnonymousAccess", DefaultLinkType.Organization => "Internal", DefaultLinkType.SpecificPeople => "Direct", _ => null },
            "defaultLinkPermission" => s.DefaultLinkPermission?.ToString(),
            "anyoneLinkExpirationDays" => I(s.AnyoneLinkExpirationDays),
            "anyoneFileLinkPermission" => s.AnyoneFileLinkPermission?.ToString(),
            "anyoneFolderLinkPermission" => s.AnyoneFolderLinkPermission?.ToString(),
            "guestExpirationRequired" => s.GuestAccessExpirationDays is { } g ? (g > 0 ? "true" : "false") : null,
            "guestAccessExpirationDays" => s.GuestAccessExpirationDays is > 0 ? I(s.GuestAccessExpirationDays) : null,
            "customScriptAllowed" => B(s.CustomScriptAllowed),
            "restrictedAccessControlEnabled" => B(s.RestrictedAccessControlEnabled),
            "microsoft365ArchiveEnabled" => B(s.Microsoft365ArchiveEnabled),
            "sensitivityLabelsEnabled" => B(s.SensitivityLabelsEnabled),
            "dlpBlocksAnyoneLinks" => B(s.DlpBlocksAnyoneLinks),
            "backupEnabled" => B(s.BackupEnabled),
            "inactiveSitePolicies" => s.InactiveSitePolicies is { } n ? (n > 0 ? "true" : "false") : null,
            "siteOwnershipPolicies" => s.SiteOwnershipPolicies is { } o ? (o > 0 ? "true" : "false") : null,
            _ => null,
        };
    }

    /// <summary>The facts a baseline setting may name, for validation.</summary>
    public static readonly IReadOnlyList<string> Facts =
    [
        "sharingLevel", "defaultLinkType", "defaultLinkPermission", "anyoneLinkExpirationDays", "anyoneFileLinkPermission", "anyoneFolderLinkPermission",
        "guestExpirationRequired", "guestAccessExpirationDays", "customScriptAllowed", "restrictedAccessControlEnabled", "microsoft365ArchiveEnabled",
        "sensitivityLabelsEnabled", "dlpBlocksAnyoneLinks", "backupEnabled", "inactiveSitePolicies", "siteOwnershipPolicies",
    ];

    /// <summary>
    /// Whether a value meets the target. For a maximum number of days (link and guest expiry), a
    /// shorter, non-zero value counts as meeting it: the baseline never loosens a stricter setting.
    /// </summary>
    public static bool Same(string current, string target, BaselineSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        if (setting.ValueType == "int" && int.TryParse(current, out var c) && int.TryParse(target, out var t))
        {
            return c > 0 && c <= t;
        }

        return string.Equals(current.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string Human(string value) => value switch
    {
        "externalUserAndGuestSharing" => "Anyone",
        "externalUserSharingOnly" => "New and existing guests",
        "existingExternalUserSharingOnly" => "Existing guests only",
        "disabled" => "Only people in the organization",
        "Internal" => "People in the organization",
        "Direct" => "Specific people",
        "AnonymousAccess" => "Anyone",
        "AllowLimitedAccess" => "Limited, web-only access",
        "BlockAccess" => "Blocked",
        "AllowFullAccess" => "Full access",
        "true" => "On",
        "false" => "Off",
        _ when value.StartsWith('{') => "On (see the values)",
        _ => value,
    };

    private static string Area(string area) => area switch
    {
        "sharing" => "Sharing",
        "links" => "Links",
        "guests" => "Guests",
        "sessions" => "Sign-in and sessions",
        "devices" => "Devices",
        "labels" => "Labels and DLP",
        "sam" => "Oversharing (SharePoint Advanced Management)",
        "lifecycle" => "Lifecycle and recovery",
        _ => area,
    };
}
