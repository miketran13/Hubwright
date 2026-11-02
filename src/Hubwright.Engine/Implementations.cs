using System.Text.Json.Nodes;
using Hubwright.Auth;
using Hubwright.Http;
using Hubwright.Implementation;
using Hubwright.Proposals;
using Hubwright.Storage;
using Hubwright.Tools;

namespace Hubwright.Engine;

/// <summary>Something that happened during a run, for the progress display.</summary>
public sealed record StepEvent(ImplementationStep Step, string Message);

/// <summary>Loading, saving and running implementation plans next to their proposal.</summary>
public static class Implementations
{
    public const string FileName = "implementation.json";

    /// <summary>The plan for a proposal folder: the saved one (with its progress), or a new one.</summary>
    public static ImplementationPlan For(HubwrightSession session, string proposalFolder)
    {
        ArgumentNullException.ThrowIfNull(session);
        var path = Path.Combine(proposalFolder, FileName);
        if (File.Exists(path))
        {
            return ImplementationPlan.FromJson(File.ReadAllText(path));
        }

        var proposal = TenantStore.LoadIntranetProposal(proposalFolder);
        var snapshot = session.Workspace.Snapshot ?? throw new InvalidOperationException("The tenant's snapshot is missing; scan again.");
        var plan = ImplementationPlanner.Build(proposal, snapshot, session.Skills.Templates);
        Save(plan, proposalFolder);
        return plan;
    }

    /// <summary>
    /// Builds the plan again from the proposal. A finished step that is still in the new plan unchanged
    /// (same operation, target and values) stays finished with its outputs; new, changed, failed and
    /// waiting steps start as pending. The old plan is kept next to it.
    /// </summary>
    public static ImplementationPlan Rebuild(HubwrightSession session, string proposalFolder)
    {
        var path = Path.Combine(proposalFolder, FileName);
        ImplementationPlan? old = null;
        if (File.Exists(path))
        {
            old = ImplementationPlan.FromJson(File.ReadAllText(path));
            File.Move(path, Path.Combine(proposalFolder, $"implementation-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"));
        }

        var plan = For(session, proposalFolder);
        if (old is not null && Carry(old, plan) > 0)
        {
            Save(plan, proposalFolder);
        }

        return plan;
    }

    /// <summary>Copies progress from an earlier plan onto unchanged steps; returns how many kept it.</summary>
    public static int Carry(ImplementationPlan from, ImplementationPlan to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var kept = 0;
        foreach (var step in to.Steps)
        {
            if (from.Find(step.Id) is { IsFinished: true } before && before.Operation == step.Operation && before.Target == step.Target
                && before.Parameters.Count == step.Parameters.Count && before.Parameters.All(p => step.Parameters.TryGetValue(p.Key, out var v) && v == p.Value))
            {
                step.Status = before.Status;
                step.Result = before.Result;
                step.Finished = before.Finished;
                step.Outputs = new Dictionary<string, string>(before.Outputs, StringComparer.Ordinal);
                kept++;
            }
        }

        return kept;
    }

    public static void Save(ImplementationPlan plan, string proposalFolder)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var path = Path.Combine(proposalFolder, FileName);
        File.WriteAllText(path + ".tmp", plan.ToJson());
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>
    /// After the theme or logo changed in the editor: theme steps carry the new theme (and run again if
    /// they had already run), and the logo step lists the new logo. Nothing is sent to the tenant here.
    /// </summary>
    public static void RefreshTheme(HubwrightSession session, string proposalFolder, IntranetProposal proposal)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(proposal);
        if (!File.Exists(Path.Combine(proposalFolder, FileName)))
        {
            return;
        }

        var plan = For(session, proposalFolder);
        var name = proposal.Blueprint.Theme().Name;
        var json = ImplementationPlanner.ThemeJson(proposal.Blueprint);
        foreach (var step in plan.Steps.Where(s => s.Operation is StepOperation.AddOrUpdateTheme or StepOperation.ApplyTheme))
        {
            if (step.Parameters.GetValueOrDefault("themeJson") == json && step.Parameters.GetValueOrDefault("name") == name)
            {
                continue;
            }

            step.Parameters["name"] = name;
            step.Parameters["themeJson"] = json;
            if (step.IsFinished)
            {
                step.Status = StepStatus.Pending;
                step.Result = "The theme changed in the editor: run this again to update the tenant.";
            }
        }

        foreach (var (step, i) in plan.Steps.Select((s, i) => (s, i)).Where(x => x.s.Operation == StepOperation.SetSiteLogo).ToList())
        {
            var fresh = ImplementationPlanner.LogoStepFor(proposal.Blueprint, step.DependsOn.FirstOrDefault() ?? step.Id, step.Title.Replace("Add the logo to ", "", StringComparison.Ordinal).Trim('"'), step.Target ?? "", step.Phase);
            var changed = fresh.Parameters["logo"] != step.Parameters.GetValueOrDefault("logo") || fresh.Parameters["square"] != step.Parameters.GetValueOrDefault("square");
            fresh.Status = changed && step.IsFinished ? StepStatus.Pending : step.Status;
            fresh.Result = changed && step.IsFinished ? "The logo changed in the editor: run this again." : step.Result;
            plan.Steps[i] = fresh;
        }

        if (plan.Steps.FindIndex(s => s.Id == "m365-header") is var bar and >= 0 && !plan.Steps[bar].IsFinished)
        {
            plan.Steps[bar] = ImplementationPlanner.SuiteBarStep(proposal.Blueprint, plan.Steps[bar].Phase).With(s => s.Hint = "m365-header");
        }

        plan.Steps.Find(s => s.Operation == StepOperation.AddOrUpdateTheme)?.With(s => s.Title = $"Add the theme \"{name}\" to the tenant");
        Save(plan, proposalFolder);
    }

    private static ImplementationStep With(this ImplementationStep step, Action<ImplementationStep> change)
    {
        change(step);
        return step;
    }

    /// <summary>Per site URL, the furthest implementation status, for the structure preview.</summary>
    public static IReadOnlyDictionary<string, string> SiteStatuses(ImplementationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in plan.Steps.Where(s => s.Target is not null && s.Kind == StepKind.Automatic).GroupBy(s => s.Target!.TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
        {
            var steps = group.ToList();
            map[group.Key] = steps.Any(s => s.Status == StepStatus.Failed) ? "Failed"
                : steps.Any(s => s.Status == StepStatus.Running) ? "Running"
                : steps.All(s => s.IsFinished) ? "Done"
                : steps.Any(s => s.IsFinished) ? "Partly done"
                : steps.Any(s => s.Status == StepStatus.Blocked) ? "Blocked"
                : "Pending";
        }

        return map;
    }

    /// <summary>A guided step the admin finished (or chose to skip) in the in-app browser.</summary>
    public static void Mark(HubwrightSession session, ImplementationPlan plan, string proposalFolder, string stepId, bool done)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        var step = plan.Find(stepId) ?? throw new InvalidOperationException($"No step {stepId}.");
        step.Status = done ? StepStatus.Done : StepStatus.Skipped;
        step.Result = done ? "Done by hand." : "Skipped.";
        step.Finished = DateTimeOffset.UtcNow;
        Save(plan, proposalFolder);
        session.Audit.Append(done ? "implement.done" : "implement.skip", $"{plan.Tenant} · {step.Id} · {step.Title}");
    }

    /// <summary>The writes the steps would send, without sending anything or reading the tenant.</summary>
    public static IReadOnlyList<(ImplementationStep Step, WriteRequest Request)> Preview(ImplementationPlan plan, IEnumerable<string> stepIds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var list = new List<(ImplementationStep, WriteRequest)>();
        foreach (var step in plan.WithDependencies(stepIds).Where(s => s.Kind == StepKind.Automatic && !s.IsFinished))
        {
            list.AddRange(ImplementationRunner.Requests(plan, step).Select(r => (step, r)));
        }

        return list;
    }

    /// <summary>A runner that only checks what is in place, with the read-only sign-in the scan used.</summary>
    public static ImplementationRunner Checker(HubwrightSession session, ImplementationPlan plan, string proposalFolder, Func<IntPtr>? window = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var auth = new TenantAuth(session.Config.Tenant, window, AccessProfile.ReadOnly);
        return new ImplementationRunner(plan, () => Save(plan, proposalFolder), new TenantHttp(auth.TokenForAsync), new TenantWriter((_, _) => throw new InvalidOperationException("A check never writes.")), session.Audit)
        {
            AssetsFolder = proposalFolder,
        };
    }

    /// <summary>A runner using the Hubwright app's write scopes.</summary>
    public static ImplementationRunner Runner(HubwrightSession session, ImplementationPlan plan, string proposalFolder, Func<IntPtr>? window = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var auth = new TenantAuth(session.Config.Tenant, window, AccessProfile.Implement);
        var audit = session.Audit;
        var writer = new TenantWriter(auth.TokenForAsync)
        {
            Log = (request, status, result) => audit.Append(status is null ? "write.send" : "write.result", status is null ? $"{request.Method} {request.Url}" : $"{status} {request.Method} {request.Url} {result}"),
        };
        return new ImplementationRunner(plan, () => Save(plan, proposalFolder), new TenantHttp(auth.TokenForAsync), writer, audit)
        {
            Tokens = (resource, ct) => auth.TokenAsync(resource, ct),
            Pnp = new PnpShell(),
            AssetsFolder = proposalFolder,
        };
    }
}

/// <summary>
/// Runs automatic steps in plan order. Each step reads first to see whether it is already in place,
/// then sends its documented write through <see cref="TenantWriter"/>, then records the outcome. A
/// failed step blocks the steps that depend on it; independent steps go on. The plan is saved after
/// every step, so a run can stop and resume.
/// </summary>
public sealed class ImplementationRunner(ImplementationPlan plan, Action save, TenantHttp read, TenantWriter write, AuditLog audit)
{
    private const string NoHub = "00000000-0000-0000-0000-000000000000";

    public TimeSpan SiteCreationTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Tokens for PnP PowerShell steps (passed to the script in an environment variable).</summary>
    public Func<TenantResource, CancellationToken, Task<string>>? Tokens { get; init; }

    /// <summary>Runs PnP PowerShell steps; null when PnP steps can't run here.</summary>
    public IPnpShell? Pnp { get; init; }

    /// <summary>The proposal folder, where the logo file lives.</summary>
    public string? AssetsFolder { get; init; }

    private bool _checking;

    private Dictionary<string, List<(DateTimeOffset At, string Outcome, int Order)>> _history = [];

    /// <summary>
    /// Reads the tenant for every unfinished step and marks the ones already in place, without writing.
    /// What SharePoint can't report (which theme a site uses, guided steps done by hand) comes from the
    /// audit log of earlier runs. PnP steps stay pending and check before they change anything.
    /// </summary>
    public async Task<int> CheckAsync(IEnumerable<string> stepIds, IProgress<StepEvent>? progress, CancellationToken cancellationToken)
    {
        var wanted = plan.WithDependencies(stepIds).Where(s => !s.IsFinished).ToList();
        var steps = wanted.Where(s => s.Kind == StepKind.Automatic).ToList();
        audit.Append("implement.check", $"{plan.Tenant} · {plan.Proposal} · {steps.Count} step(s)");
        _history = History(audit, plan.Tenant);
        var found = 0;
        foreach (var step in wanted.Where(s => s.Kind == StepKind.Guided))
        {
            if (_history.GetValueOrDefault(step.Id)?.LastOrDefault() is { Outcome: "done" or "skipped" } last)
            {
                Finish(step, last.Outcome == "done" ? StepStatus.Done : StepStatus.Skipped, $"{(last.Outcome == "done" ? "Done by hand" : "Skipped")} on {last.At.ToLocalTime():yyyy-MM-dd} (from the audit log).", progress);
                found++;
            }
        }

        _checking = true;
        try
        {
            foreach (var step in steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new StepEvent(step, "Checking…"));
                string result;
                try
                {
                    var (status, message) = await ExecuteAsync(step, cancellationToken).ConfigureAwait(false);
                    if (status == StepStatus.AlreadyDone)
                    {
                        Finish(step, status, message, progress);
                        found++;
                        continue;
                    }

                    result = message;
                }
                catch (ConnectorException ex) when (ex.IsAccessDenied)
                {
                    result = $"Couldn't check: SharePoint refused the read ({(int)ex.Status}). It is checked again when it runs.";
                }
                catch (Exception ex) when (ex is ConnectorException or InvalidOperationException or HttpRequestException or SignInRequiredException or System.Text.Json.JsonException)
                {
                    result = $"Couldn't check: {ex.Message}";
                }

                step.Status = StepStatus.Pending;
                step.Result = result;
                save();
                progress?.Report(new StepEvent(step, result));
            }
        }
        finally
        {
            _checking = false;
        }

        audit.Append("implement.check.end", $"{plan.Tenant} · {found} of {steps.Count} already in place");
        return found;
    }

    public async Task RunAsync(IEnumerable<string> stepIds, IProgress<StepEvent>? progress, CancellationToken cancellationToken)
    {
        var steps = plan.WithDependencies(stepIds).Where(s => s.Kind == StepKind.Automatic && !s.IsFinished).ToList();
        audit.Append("implement.start", $"{plan.Tenant} · {plan.Proposal} · {steps.Count} step(s): {string.Join(", ", steps.Select(s => s.Id))}");
        foreach (var step in steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var waiting = step.DependsOn.Select(plan.Find).OfType<ImplementationStep>().FirstOrDefault(d => !d.IsFinished);
            if (waiting is not null)
            {
                Finish(step, StepStatus.Blocked, waiting.Kind == StepKind.Guided ? $"Waiting for \"{waiting.Title}\" (a guided step): do it, mark it done, then run this again." : $"Waiting for \"{waiting.Title}\" ({waiting.Status}).", progress);
                continue;
            }

            step.Status = StepStatus.Running;
            step.Result = null;
            progress?.Report(new StepEvent(step, "Running…"));
            save();
            try
            {
                var (status, result) = await ExecuteAsync(step, cancellationToken).ConfigureAwait(false);
                Finish(step, status, result, progress);
            }
            catch (OperationCanceledException)
            {
                Finish(step, StepStatus.Pending, "Cancelled before it finished; run it again to check.", progress);
                throw;
            }
            catch (ConnectorException ex) when (ex.IsAccessDenied)
            {
                Finish(step, StepStatus.Failed, Denied(step, ex), progress);
            }
            catch (Exception ex) when (ex is ConnectorException or InvalidOperationException or HttpRequestException or SignInRequiredException or TimeoutException or System.Text.Json.JsonException)
            {
                Finish(step, StepStatus.Failed, ex.Message, progress);
            }
        }

        audit.Append("implement.end", $"{plan.Tenant} · {steps.Count(s => s.Status is StepStatus.Done or StepStatus.AlreadyDone)} done, {steps.Count(s => s.Status == StepStatus.Failed)} failed, {steps.Count(s => s.Status == StepStatus.Blocked)} blocked");
    }

    /// <summary>Per step id, the outcomes the audit log recorded for this tenant, oldest first.</summary>
    internal static Dictionary<string, List<(DateTimeOffset At, string Outcome, int Order)>> History(AuditLog audit, string tenant)
    {
        var history = new Dictionary<string, List<(DateTimeOffset, string, int)>>(StringComparer.Ordinal);
        var order = 0;
        foreach (var entry in audit.Read())
        {
            order++;
            var parts = entry.Detail.Split(" · ");
            if (parts.Length < 2 || !string.Equals(parts[0], tenant, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var outcome = entry.Action switch
            {
                "implement.step" when parts.Length >= 3 => parts[2],
                "implement.done" => "done",
                "implement.skip" => "skipped",
                _ => null,
            };
            if (outcome is not null)
            {
                (history.TryGetValue(parts[1], out var list) ? list : history[parts[1]] = []).Add((entry.At, outcome, order));
            }
        }

        return history;
    }

    /// <summary>A refusal explained: who lacks which right, and the two ways forward.</summary>
    private static string Denied(ImplementationStep step, ConnectorException ex)
    {
        var where = step.Target ?? "the tenant";
        var why = step.Operation switch
        {
            StepOperation.SetSharing or StepOperation.AddOrUpdateTheme => "This needs the SharePoint Administrator role for the signed-in account, and admin consent to the app's write permissions.",
            StepOperation.CreateSite => "The signed-in account may not create sites (site creation can be limited to admins), or the write permissions aren't consented.",
            _ => $"The signed-in account has no rights on {where}. Being a SharePoint admin doesn't make you a member of each site, and this request works on the site itself.",
        };
        return $"SharePoint refused ({(int)ex.Status}): {why} Do it by hand in the Step tab (the admin center route opens there), or make yourself a site admin of the site and run the step again.";
    }
    private void Finish(ImplementationStep step, StepStatus status, string? result, IProgress<StepEvent>? progress)
    {
        step.Status = status;
        step.Result = result;
        step.Finished = DateTimeOffset.UtcNow;
        save();
        audit.Append("implement.step", $"{plan.Tenant} · {step.Id} · {status} · {result}");
        progress?.Report(new StepEvent(step, result ?? status.ToString()));
    }

    /// <summary>The documented write a step sends. Placeholders such as a new hub's id stay visible until they are known.</summary>
    public static IReadOnlyList<WriteRequest> Requests(ImplementationPlan plan, ImplementationStep step)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(step);
        string P(string key) => step.Parameters.TryGetValue(key, out var v) ? ImplementationPlanner.Resolve(plan, v) ?? v : "";
        var root = plan.RootUrl;
        var target = step.Target ?? root;
        return step.Operation switch
        {
            StepOperation.SetSharing => [new("PATCH", "https://graph.microsoft.com/v1.0/admin/sharepoint/settings", Json(new JsonObject { ["sharingCapability"] = P("sharingCapability") }), false)],
            StepOperation.SetTenantSetting => [new("PATCH", "https://graph.microsoft.com/v1.0/admin/sharepoint/settings", Json(new JsonObject { [P("property")] = Typed(P("value"), P("type")) }), false)],
            StepOperation.AddOrUpdateTheme => [new("POST", root + "/_api/thememanager/AddTenantTheme", Json(new JsonObject { ["name"] = P("name"), ["themeJson"] = P("themeJson") }), false)],
            StepOperation.ApplyTheme => [new("POST", target + "/_api/thememanager/ApplyTheme", Json(new JsonObject { ["name"] = P("name"), ["themeJson"] = P("themeJson") }), false)],
            StepOperation.CreateSite => [new("POST", root + "/_api/SPSiteManager/create", Json(CreateBody(step, P)), false)],
            StepOperation.RegisterHub => [new("POST", target + "/_api/site/RegisterHubSite", null, false), new("POST", $"{root}/_api/HubSites/GetById?hubSiteId='{{{step.Id}.hubSiteId}}'", Json(new JsonObject { ["Title"] = P("title") }), true)],
            StepOperation.RenameHub => [new("POST", $"{root}/_api/HubSites/GetById?hubSiteId='{P("hubSiteId")}'", Json(new JsonObject { ["Title"] = P("title") }), true)],
            StepOperation.AssociateWithHub => [new("POST", $"{target}/_api/site/JoinHubSite('{P("hubSiteId")}')", null, false)],
            StepOperation.DisassociateFromHub => [new("POST", $"{target}/_api/site/JoinHubSite('{NoHub}')", null, false)],
            var op when PnpScripts.IsPnp(op) => [new("PnP", PnpScripts.ConnectUrl(plan, step), PnpScripts.Script(plan, step), false)],
            _ => [],
        };
    }

    private async Task<(StepStatus, string)> ExecuteAsync(ImplementationStep step, CancellationToken ct)
    {
        var root = plan.RootUrl;
        var target = step.Target ?? root;
        string P(string key) => step.Parameters.TryGetValue(key, out var v)
            ? ImplementationPlanner.Resolve(plan, v) ?? throw new InvalidOperationException($"{key} is not known yet: {v} has not run.")
            : "";
        async Task Send(WriteRequest r) => await write.SendAsync(r.Method, r.Url, r.Body, ct, r.Merge).ConfigureAwait(false);

        switch (step.Operation)
        {
            case StepOperation.SetSharing:
            {
                var now = Text(await read.GetAsync("https://graph.microsoft.com/v1.0/admin/sharepoint/settings", ct).ConfigureAwait(false), "sharingCapability");
                if (string.Equals(now, P("sharingCapability"), StringComparison.OrdinalIgnoreCase))
                {
                    return (StepStatus.AlreadyDone, $"Sharing is already {now}.");
                }

                if (_checking)
                {
                    return (StepStatus.Pending, $"Not in place: sharing is {now ?? "unknown"}; this sets {P("sharingCapability")}.");
                }

                await Send(Requests(plan, step)[0]).ConfigureAwait(false);
                return (StepStatus.Done, $"Sharing changed from {now ?? "unknown"} to {P("sharingCapability")}.");
            }

            case StepOperation.SetTenantSetting:
            {
                var property = P("property");
                var settings = await read.GetAsync("https://graph.microsoft.com/v1.0/admin/sharepoint/settings", ct).ConfigureAwait(false);
                var current = (settings as JsonObject)?[property];
                var now = current is null ? null : current is JsonValue ? current.ToString() : current.ToJsonString();
                var want = Typed(P("value"), P("type"));
                if (now is not null && SameSetting(current, want))
                {
                    return (StepStatus.AlreadyDone, $"{property} is already {now}.");
                }

                if (_checking)
                {
                    return (StepStatus.Pending, $"Not in place: {property} is {now ?? "not reported"}.");
                }

                step.Outputs["previous"] = now ?? "";
                await Send(Requests(plan, step)[0]).ConfigureAwait(false);
                return (StepStatus.Done, $"{property} changed from {now ?? "unknown"} to {P("value")}.");
            }

            case StepOperation.AddOrUpdateTheme:
            {
                var options = await read.PostReadOnlyAsync(root + "/_api/thememanager/GetTenantThemingOptions", ct).ConfigureAwait(false);
                var theme = (Find(options, "themePreviews") as JsonArray ?? []).FirstOrDefault(t => string.Equals(Text(t, "name"), P("name"), StringComparison.Ordinal));
                var exists = theme is not null;
                if (exists && SamePalette(Text(theme, "themeJson"), P("themeJson")))
                {
                    return (StepStatus.AlreadyDone, "The tenant already has this theme with these colours.");
                }

                if (_checking)
                {
                    return (StepStatus.Pending, exists ? "Not in place: the tenant's theme of this name has other colours." : "Not in place: the tenant has no theme of this name.");
                }

                var body = Json(new JsonObject { ["name"] = P("name"), ["themeJson"] = P("themeJson") });
                await write.SendAsync("POST", root + (exists ? "/_api/thememanager/UpdateTenantTheme" : "/_api/thememanager/AddTenantTheme"), body, ct).ConfigureAwait(false);
                return (StepStatus.Done, exists ? "Theme updated." : "Theme added to the tenant.");
            }

            case StepOperation.ApplyTheme when _checking:
            {
                // SharePoint doesn't report which theme a site uses, so look for Hubwright's own successful
                // run, with the tenant theme confirmed unchanged and not updated since.
                var theme = plan.Steps.FirstOrDefault(s => s.Operation == StepOperation.AddOrUpdateTheme);
                var applied = _history.GetValueOrDefault(step.Id)?.LastOrDefault(e => e.Outcome is "Done" or "AlreadyDone");
                var updated = theme is null ? null : _history.GetValueOrDefault(theme.Id)?.LastOrDefault(e => e.Outcome == "Done").Order;
                return applied is { } a && theme is { IsFinished: true } && (updated is null || updated < a.Order)
                    ? (StepStatus.AlreadyDone, $"Applied by Hubwright on {a.At.ToLocalTime():yyyy-MM-dd} (audit log); the theme hasn't changed since.")
                    : (StepStatus.Pending, "Not applied by Hubwright with this theme yet (SharePoint doesn't report a site's theme).");
            }

            case StepOperation.ApplyTheme:
                await Send(Requests(plan, step)[0]).ConfigureAwait(false);
                return (StepStatus.Done, "Theme applied.");

            case StepOperation.CreateSite:
            {
                var url = P("url");
                var status = await SiteStatusAsync(url, ct).ConfigureAwait(false);
                if (status == 2)
                {
                    step.Outputs["url"] = url;
                    return (StepStatus.AlreadyDone, "The site already exists.");
                }

                if (_checking)
                {
                    return (StepStatus.Pending, status == 4 ? "A site with this URL exists but Hubwright can't confirm it is the one planned: check it." : "Not created yet.");
                }

                var created = await write.SendAsync("POST", root + "/_api/SPSiteManager/create", Json(CreateBody(step, P)), ct).ConfigureAwait(false);
                status = Number(created, "SiteStatus") ?? 1;
                var deadline = DateTimeOffset.UtcNow + SiteCreationTimeout;
                while (status == 1 && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                    status = await SiteStatusAsync(url, ct).ConfigureAwait(false);
                }

                step.Outputs["url"] = url;
                return status switch
                {
                    2 => (StepStatus.Done, "Site created."),
                    1 => (StepStatus.Failed, "Still provisioning after the timeout; run the step again later to check."),
                    4 => (StepStatus.Failed, "A site with this URL already exists but Hubwright can't confirm it is the one planned. Check it, then mark the step skipped or change the URL."),
                    _ => (StepStatus.Failed, $"SharePoint reported site status {status} (3 = error while provisioning)."),
                };
            }

            case StepOperation.RegisterHub:
            {
                var id = await HubIdAsync(target, ct).ConfigureAwait(false);
                var registered = id is null;
                if (registered && _checking)
                {
                    return (StepStatus.Pending, "Not a hub yet.");
                }

                if (registered)
                {
                    var hub = await write.SendAsync("POST", target + "/_api/site/RegisterHubSite", null, ct).ConfigureAwait(false);
                    id = Text(hub, "ID") ?? Text(hub, "Id") ?? await HubIdAsync(target, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The hub was registered but SharePoint did not return its id.");
                }

                step.Outputs["hubSiteId"] = id!;
                var renamed = false;
                if (step.Parameters.TryGetValue("title", out var title))
                {
                    var current = await read.GetAsync($"{root}/_api/HubSites/GetById?hubSiteId='{id}'", ct, sharePointVerbose: true).ConfigureAwait(false);
                    if (!string.Equals(Text(current, "Title"), title, StringComparison.Ordinal))
                    {
                        if (_checking)
                        {
                            return (StepStatus.Pending, $"Already a hub, named \"{Text(current, "Title")}\"; this renames it to \"{title}\".");
                        }

                        await write.SendAsync("POST", $"{root}/_api/HubSites/GetById?hubSiteId='{id}'", Json(new JsonObject { ["Title"] = title }), ct, merge: true).ConfigureAwait(false);
                        renamed = true;
                    }
                }

                return registered ? (StepStatus.Done, $"Registered as a hub ({id}).")
                    : renamed ? (StepStatus.Done, $"Already a hub; renamed to \"{title}\".")
                    : (StepStatus.AlreadyDone, "Already a hub with this name.");
            }

            case StepOperation.RenameHub:
            {
                var hub = await read.GetAsync($"{root}/_api/HubSites/GetById?hubSiteId='{P("hubSiteId")}'", ct, sharePointVerbose: true).ConfigureAwait(false);
                if (string.Equals(Text(hub, "Title"), P("title"), StringComparison.Ordinal))
                {
                    return (StepStatus.AlreadyDone, "The hub already has this name.");
                }

                if (_checking)
                {
                    return (StepStatus.Pending, $"Not in place: the hub is named \"{Text(hub, "Title")}\".");
                }

                await Send(Requests(plan, step)[0]).ConfigureAwait(false);
                return (StepStatus.Done, $"Renamed from \"{Text(hub, "Title")}\".");
            }

            case StepOperation.AssociateWithHub:
            case StepOperation.DisassociateFromHub:
            {
                var want = step.Operation == StepOperation.AssociateWithHub ? P("hubSiteId") : NoHub;
                var site = await read.GetAsync(target + "/_api/site?$select=HubSiteId", ct, sharePointVerbose: true).ConfigureAwait(false);
                if (string.Equals(Text(site, "HubSiteId"), want, StringComparison.OrdinalIgnoreCase))
                {
                    return (StepStatus.AlreadyDone, step.Operation == StepOperation.AssociateWithHub ? "Already in this hub." : "Already outside every hub.");
                }

                if (_checking)
                {
                    return (StepStatus.Pending, step.Operation == StepOperation.AssociateWithHub ? "Not in this hub yet." : "Still in a hub.");
                }

                await Send(Requests(plan, step)[0]).ConfigureAwait(false);
                return (StepStatus.Done, step.Operation == StepOperation.AssociateWithHub ? "Associated." : "Taken out of its hub.");
            }

            case StepOperation.ProvisionPackage when _checking:
                return step.Target is { } packaged && await SiteStatusAsync(packaged, ct).ConfigureAwait(false) == 2
                    ? (StepStatus.AlreadyDone, "The site already exists; the package was not applied again.")
                    : (StepStatus.Pending, "Not created yet.");

            case var op when PnpScripts.IsPnp(op) && _checking:
                return (StepStatus.Pending, "Checked when it runs: the PnP script reads before it changes anything.");

            case var op when PnpScripts.IsPnp(op):
                return await RunPnpAsync(step, ct).ConfigureAwait(false);

            default:
                return (StepStatus.Skipped, "Nothing to run.");
        }
    }

    private async Task<(StepStatus, string)> RunPnpAsync(ImplementationStep step, CancellationToken ct)
    {
        if (Pnp is null || Tokens is null)
        {
            throw new InvalidOperationException("PnP PowerShell steps can't run here. Install PowerShell 7 and PnP PowerShell (Setup → Prerequisites), or do it by hand in the Step tab.");
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (step.Operation)
        {
            case StepOperation.SetSiteLogo:
            {
                var logo = step.Parameters.GetValueOrDefault("logo");
                if (string.IsNullOrWhiteSpace(logo))
                {
                    return (StepStatus.Skipped, "No logo in the proposal yet: add one in Theme and logo, then run this again.");
                }

                var path = Path.GetFullPath(Path.Combine(AssetsFolder ?? "", logo));
                environment[PnpScripts.LogoVariable] = File.Exists(path) ? path : throw new InvalidOperationException($"The logo file {path} is missing: add it again in Theme and logo.");
                break;
            }

            case StepOperation.ProvisionPackage when step.Target is { } url && await SiteStatusAsync(url, ct).ConfigureAwait(false) == 2:
                return (StepStatus.AlreadyDone, "The site already exists; the package was not applied again.");
        }

        var script = PnpScripts.Script(plan, step);
        environment[PnpScripts.TokenVariable] = await Tokens(PnpScripts.NeedsAdmin(step.Operation) ? TenantResource.SharePointAdmin : TenantResource.SharePoint, ct).ConfigureAwait(false);
        audit.Append("pnp.run", $"{step.Operation} {step.Target}");
        var run = await Pnp.RunAsync(script, environment, ct).ConfigureAwait(false);
        var error = run.Failure ?? string.Join(" ", run.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.StartsWith('+') && !l.StartsWith("At ", StringComparison.Ordinal)).Take(4));
        audit.Append("pnp.result", $"{run.ExitCode} {step.Operation} {step.Target} {run.Result ?? error}");
        if (run.ExitCode == 0 && run.Result is not null)
        {
            if (step.Operation is StepOperation.SetTenantPolicy or StepOperation.PublishDesignPage)
            {
                if (run.Result.StartsWith("Already", StringComparison.Ordinal))
                {
                    return (StepStatus.AlreadyDone, run.Result);
                }

                step.Outputs["previous"] = run.Previous ?? "";
            }

            return (StepStatus.Done, run.Result);
        }

        if (error.Contains("403", StringComparison.Ordinal) || error.Contains("401", StringComparison.Ordinal) || error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) || error.Contains("Access denied", StringComparison.OrdinalIgnoreCase) || error.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
        {
            return (StepStatus.Failed, $"SharePoint refused: {(PnpScripts.NeedsAdmin(step.Operation) || step.Operation is StepOperation.ApplySiteTemplate or StepOperation.ProvisionPackage ? "this needs the SharePoint Administrator role" : $"the signed-in account has no rights on {step.Target}")}. Do it by hand in the Step tab, or fix the rights and run again. ({error})");
        }

        return (StepStatus.Failed, $"PnP PowerShell stopped (exit {run.ExitCode}): {(error.Length > 0 ? error : "no message")}");
    }

    private static JsonObject CreateBody(ImplementationStep step, Func<string, string> p)
    {
        var request = new JsonObject
        {
            ["Title"] = p("title"),
            ["Url"] = p("url"),
            ["Lcid"] = int.TryParse(p("lcid"), out var lcid) ? lcid : 1033,
            ["ShareByEmailEnabled"] = false,
            ["Description"] = p("description"),
            ["WebTemplate"] = p("webTemplate"),
        };
        if (step.Parameters.ContainsKey("siteDesignId"))
        {
            request["SiteDesignId"] = p("siteDesignId");
        }

        return new JsonObject { ["request"] = request };
    }

    private async Task<int?> SiteStatusAsync(string url, CancellationToken ct)
    {
        var node = await read.GetAsync($"{plan.RootUrl}/_api/SPSiteManager/status?url='{Uri.EscapeDataString(url)}'", ct, sharePointVerbose: true).ConfigureAwait(false);
        return Number(node, "SiteStatus");
    }

    private async Task<string?> HubIdAsync(string siteUrl, CancellationToken ct)
    {
        var hubs = await read.GetAsync(plan.RootUrl + "/_api/HubSites", ct, sharePointVerbose: true).ConfigureAwait(false);
        var list = Find(hubs, "value") as JsonArray ?? Find(hubs, "results") as JsonArray ?? [];
        return list.Where(h => string.Equals(Text(h, "SiteUrl")?.TrimEnd('/'), siteUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            .Select(h => Text(h, "ID") ?? Text(h, "Id")).FirstOrDefault(id => id is not null);
    }

    private static string Json(JsonNode node) => node.ToJsonString();

    /// <summary>A baseline value as JSON: a boolean, a number, an object (json) or a string.</summary>
    internal static JsonNode? Typed(string value, string type) => type switch
    {
        "bool" => JsonValue.Create(value.Equals("true", StringComparison.OrdinalIgnoreCase)),
        "int" => JsonValue.Create(int.Parse(value, System.Globalization.CultureInfo.InvariantCulture)),
        "json" => JsonNode.Parse(value),
        _ => JsonValue.Create(value),
    };

    /// <summary>Whether Graph's value equals the target: same text for values, same properties for objects.</summary>
    internal static bool SameSetting(JsonNode? current, JsonNode? want)
    {
        if (current is JsonObject a && want is JsonObject b)
        {
            return b.All(p => a.TryGetPropertyValue(p.Key, out var v) && string.Equals(v?.ToString(), p.Value?.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        return string.Equals(current?.ToString(), want?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether two theme JSONs have the same palette, whatever the key order or hex case.</summary>
    internal static bool SamePalette(string? a, string? b)
    {
        static Dictionary<string, string>? Palette(string? json)
        {
            try
            {
                var node = json is null ? null : JsonNode.Parse(json);
                return (Find(node, "palette") ?? node) is JsonObject palette
                    ? palette.ToDictionary(p => p.Key, p => p.Value?.ToString().ToLowerInvariant() ?? "", StringComparer.OrdinalIgnoreCase)
                    : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }

        var (x, y) = (Palette(a), Palette(b));
        return x is not null && y is not null && x.Count == y.Count && x.All(p => y.TryGetValue(p.Key, out var v) && v == p.Value);
    }

    /// <summary>A property anywhere in the first levels of a response, whatever OData flavour SharePoint chose.</summary>
    internal static JsonNode? Find(JsonNode? node, string name, int depth = 0)
    {
        if (node is not JsonObject obj || depth > 3)
        {
            return null;
        }

        foreach (var (key, value) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return obj.Select(kv => Find(kv.Value, name, depth + 1)).FirstOrDefault(v => v is not null);
    }

    private static string? Text(JsonNode? node, string name) => Find(node, name) is JsonValue v ? v.ToString() : null;

    private static int? Number(JsonNode? node, string name) => Find(node, name) is JsonValue v && int.TryParse(v.ToString(), out var n) ? n : null;
}
