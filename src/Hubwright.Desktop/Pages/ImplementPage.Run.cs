using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Auth;
using Hubwright.Engine;
using Hubwright.Implementation;
using Hubwright.Snapshot;
using Hubwright.Storage;

namespace Hubwright.Desktop.Pages;

/// <summary>Preview, run, check, start over and restore: the actions on the selected steps.</summary>
public sealed partial class ImplementPage
{
    private async Task PreviewAsync()
    {
        if (_plan is null)
        {
            return;
        }

        var selected = Checked();
        var requests = Implementations.Preview(_plan, selected.Count > 0 ? selected : _plan.Steps.Where(s => s.Kind == StepKind.Automatic).Select(s => s.Id));
        var text = string.Join("\n\n", requests.Select(r => $"{r.Step.Id}\n{r.Request.Method}{(r.Request.Merge ? " (MERGE)" : "")} {r.Request.Url}{(r.Request.Body is null ? "" : "\n" + (r.Request.Body.Length > 300 ? r.Request.Body[..300] + "…" : r.Request.Body))}"));
        await Ui.Message(XamlRoot, $"{requests.Count} request(s), nothing sent", text.Length == 0 ? "No automatic steps are selected or pending." : text + "\n\nValues in {braces} come from earlier steps when they run.");
    }

    private async Task RunAsync()
    {
        if (_plan is null || _folder is null)
        {
            return;
        }

        if (_sample)
        {
            await Ui.Message(XamlRoot, "Preview only", "This tenant is sample or design-only data. Scan your tenant, create a plan from that scan, then build it.");
            return;
        }

        var selected = Checked();
        var automatic = _plan.WithDependencies(selected).Where(s => s.Kind == StepKind.Automatic && !s.IsFinished).ToList();
        if (automatic.Count == 0)
        {
            await Ui.Message(XamlRoot, "Nothing to run", "Select automatic steps (guided steps are done in the pane on the right and marked done there).");
            return;
        }

        var confirm = new TextBox { PlaceholderText = _session.Config.Tenant.Name ?? "" };
        var list = string.Join("\n", automatic.Take(40).Select(s => $"• {s.Title} ({s.Risk.ToString().ToLowerInvariant()})")) + (automatic.Count > 40 ? $"\n… and {automatic.Count - 40} more" : "");
        var extra = automatic.Count - selected.Count(id => automatic.Any(a => a.Id == id));
        var dialog = new ContentDialog
        {
            RequestedTheme = Ui.Theme,
            Title = $"Change {_plan.RootUrl}?",
            Content = new ScrollViewer
            {
                MaxHeight = 480,
                Content = Ui.Stack(10,
                    Ui.Text($"{automatic.Count} automatic step(s){(extra > 0 ? $", including {extra} that the selected steps depend on" : "")}:"),
                    Ui.Text(list, 13),
                    Ui.Muted("Each step checks first and skips what is already in place. Every write goes to the Audit log. Type the tenant name to continue."),
                    confirm),
            },
            PrimaryButtonText = "Run",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !string.Equals(confirm.Text.Trim(), _session.Config.Tenant.Name, StringComparison.OrdinalIgnoreCase))
        {
            _status.Text = "Cancelled. Nothing was changed.";
            return;
        }

        var runner = Implementations.Runner(_session, _plan, _folder, () => App.Current.Window.Handle);
        var progress = new Progress<StepEvent>(e =>
        {
            Activity.Report(_status.Text = $"{e.Step.Title}: {e.Message}");
            if (_rows.TryGetValue(e.Step.Id, out var row))
            {
                row.Result.Text = e.Step.Result ?? e.Message;
                if (row.Badge.Child is TextBlock badge)
                {
                    var fresh = StatusBadge(e.Step);
                    badge.Text = ((TextBlock)fresh.Child).Text;
                    row.Badge.Background = fresh.Background;
                }
            }
        });
        _run = new CancellationTokenSource();
        _stopButton.Visibility = Visibility.Visible;
        _runButton.IsEnabled = false;
        try
        {
            await Task.Run(() => runner.RunAsync(selected, progress, _run.Token));
            _status.Text = $"Finished: {automatic.Count(s => s.Status is StepStatus.Done or StepStatus.AlreadyDone)} done, {automatic.Count(s => s.Status == StepStatus.Failed)} failed, {automatic.Count(s => s.Status == StepStatus.Blocked)} waiting.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Stopped. Finished steps stay finished; run again to continue.";
        }
        finally
        {
            _run.Dispose();
            _run = null;
            _stopButton.Visibility = Visibility.Collapsed;
            _runButton.IsEnabled = true;
            Fill();
            _ = PreviewsAsync();
        }
    }

    private async Task RebuildAsync()
    {
        if (_folder is null || !await Ui.Confirm(XamlRoot, "Start over?", _security
            ? "The baseline is compared again with the latest scan. Finished steps that haven't changed stay finished, and recorded earlier values are kept for Restore; then Hubwright reads the tenant (without changing anything)."
            : "The plan is rebuilt from the latest proposal. Finished steps that haven't changed stay finished; then Hubwright reads the tenant (without changing anything) and marks what is already in place.", "Start over"))
        {
            return;
        }

        if (_security)
        {
            _folder = SecurityBaselines.Folder(_session, Profile);
            _plan = SecurityBaselines.For(_session, Profile, rebuild: true);
        }
        else
        {
            _plan = Implementations.Rebuild(_session, _folder);
        }

        Fill();
        _ = PreviewsAsync();
        await CheckAsync();
    }

    /// <summary>Security mode: switch to the steps that put back what this profile changed.</summary>
    private async Task RestoreAsync()
    {
        try
        {
            _plan = SecurityBaselines.Restore(_session, Profile);
            _folder = SecurityBaselines.RestoreFolder(_session, Profile);
            Fill();
            _status.Text = $"Restore: {_plan.Steps.Count} setting(s) Hubwright changed, with the values it recorded before. Select them and run; pick another profile to leave.";
        }
        catch (InvalidOperationException ex)
        {
            await Ui.Message(XamlRoot, "Nothing to restore", ex.Message);
        }
    }

    /// <summary>Reads the tenant and marks the steps already in place; never writes.</summary>
    private async Task CheckAsync()
    {
        if (_plan is null || _folder is null || _run is not null)
        {
            return;
        }

        if (_sample)
        {
            _status.Text = "Sample or design-only data: there is no tenant to check.";
            return;
        }

        var checker = Implementations.Checker(_session, _plan, _folder, () => App.Current.Window.Handle);
        var progress = new Progress<StepEvent>(e => Activity.Report(_status.Text = $"Checking {e.Step.Title}…"));
        using var running = Activity.IsBusy ? null : Activity.Begin("Checking the tenant (read only)");
        _run = new CancellationTokenSource();
        _stopButton.Visibility = Visibility.Visible;
        _runButton.IsEnabled = false;
        try
        {
            var pending = _plan.Steps.Count(s => !s.IsFinished);
            var found = await Task.Run(() => checker.CheckAsync(_plan.Steps.Select(s => s.Id), progress, _run.Token));
            _status.Text = $"Checked the tenant: {found} of {pending} step(s) already in place. Nothing was changed. The rest is checked again when it runs.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Check stopped. Nothing was changed.";
        }
        catch (Exception ex) when (ex is SignInRequiredException or InvalidOperationException)
        {
            _status.Text = $"Couldn't check the tenant: {ex.Message}";
        }
        finally
        {
            _run.Dispose();
            _run = null;
            _stopButton.Visibility = Visibility.Collapsed;
            _runButton.IsEnabled = true;
            Fill();
            _ = PreviewsAsync();
        }
    }
}
