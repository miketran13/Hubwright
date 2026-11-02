using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hubwright.Auth;
using Hubwright.Engine;
using Hubwright.Implementation;
using Hubwright.Snapshot;
using Hubwright.Storage;

namespace Hubwright.Desktop.Pages;

/// <summary>The right-hand Step tab: what a step does, why, its request or script, and guided steps in the in-app browser.</summary>
public sealed partial class ImplementPage
{
    private void Show(ImplementationStep step)
    {
        var panel = Ui.Stack(10,
            Ui.Row(KindBadge(step), RiskBadge(step), StatusBadge(step)),
            Ui.Title(step.Title));
        if (!string.IsNullOrWhiteSpace(step.Detail))
        {
            panel.Children.Add(Ui.Text(step.Detail));
        }

        if (step.Result is not null)
        {
            panel.Children.Add(Ui.InfoBar("Result", step.Result, step.Status == StepStatus.Failed ? InfoBarSeverity.Error : step.Status is StepStatus.Done or StepStatus.AlreadyDone ? InfoBarSeverity.Success : InfoBarSeverity.Informational));
        }

        if (step.Target is { } target)
        {
            panel.Children.Add(Ui.Row(
                Ui.Muted(target),
                Ui.Button("Open site here", () =>
                {
                    (_browser.Parent as Panel)?.Children.Remove(_browser);
                    panel.Children.Add(_browser);
                    _ = NavigateAsync(target);
                }, glyph: Glyphs.Open),
                Ui.Button("Open in my browser", () => Ui.Open(target), glyph: Glyphs.Open)));
        }

        if (_hints.For(step) is { } hint)
        {
            panel.Children.Add(HintCard(hint));
        }

        if (step.DependsOn.Count > 0 && _plan is not null)
        {
            panel.Children.Add(Ui.Muted("After: " + string.Join(", ", step.DependsOn.Select(d => _plan.Find(d)?.Title ?? d))));
        }

        if (step.Kind == StepKind.Automatic && _plan is not null)
        {
            panel.Children.Add(Ui.Heading(PnpScripts.IsPnp(step.Operation) ? "What Hubwright runs (PnP PowerShell, with your sign-in)" : "What Hubwright sends"));
            foreach (var request in ImplementationRunner.Requests(_plan, step))
            {
                panel.Children.Add(Ui.Area("", request.Method == "PnP" ? request.Body ?? "" : $"{request.Method}{(request.Merge ? " (X-HTTP-Method: MERGE)" : "")} {request.Url}\n{request.Body ?? "(no body)"}", request.Method == "PnP" ? 240 : 120).With(a => a.IsReadOnly = true));
            }

            panel.Children.Add(Ui.Muted("It reads first and skips the write when the change is already in place."));
            if (step.Source is not null)
            {
                panel.Children.Add(Ui.Link("The Microsoft documentation this follows", step.Source));
            }

            if (ImplementationPlanner.Fallback(_plan, step) is { } fallback && !step.IsFinished)
            {
                var manual = Ui.Stack(8);
                foreach (var line in fallback.Instructions)
                {
                    manual.Children.Add(Ui.Row(Ui.Button("Copy", () => Copy(line)), Ui.Text(line, 13)));
                }

                manual.Children.Add(Ui.Row(
                    Ui.Button("Open it here", () => _ = NavigateAsync(fallback.Url), glyph: Glyphs.Open),
                    Ui.Button("Mark done", () => Mark(step, done: true), accent: true, glyph: Glyphs.Check),
                    Ui.Button("Skip", () => Mark(step, done: false), glyph: Glyphs.Cancel)));
                var failed = step.Status == StepStatus.Failed;
                panel.Children.Add(new Expander
                {
                    Header = failed ? "Do it by hand instead" : "Prefer to do it by hand?",
                    IsExpanded = failed,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = manual,
                });
                (_browser.Parent as Panel)?.Children.Remove(_browser);
                panel.Children.Add(_browser);
                if (failed)
                {
                    _ = NavigateAsync(fallback.Url);
                }
            }
        }
        else
        {
            panel.Children.Add(Ui.Heading("What to do"));
            foreach (var line in step.Instructions)
            {
                panel.Children.Add(Ui.Row(
                    Ui.Button("Copy", () => Copy(line.Contains(": http", StringComparison.Ordinal) ? line[(line.IndexOf(": http", StringComparison.Ordinal) + 2)..] : line.Trim())),
                    Ui.Text(line, 13)));
            }

            panel.Children.Add(Ui.Row(
                Ui.Button("Mark done", () => Mark(step, done: true), accent: true, glyph: Glyphs.Check),
                Ui.Button("Skip", () => Mark(step, done: false), glyph: Glyphs.Cancel),
                Ui.Button("Back", () => _browser.GoBack()),
                Ui.Button("Open in my browser", () => Ui.Open(_browser.Source?.ToString() ?? step.GuideUrl ?? ""), glyph: Glyphs.Open)));
            if (step.GuideUrl is not null)
            {
                (_browser.Parent as Panel)?.Children.Remove(_browser);
                panel.Children.Add(_browser);
                _ = NavigateAsync(step.GuideUrl);
            }
        }

        _detail.Content = panel;
        _tabs.SelectedIndex = 0;
    }

    /// <summary>What, why (with Microsoft's words), how and an example, for the selected step.</summary>
    private Border HintCard(Hints.Hint hint)
    {
        var body = Ui.Stack(6, Ui.Strong("About this step"), Ui.Muted("WHAT"), Ui.Text(hint.What, 13), Ui.Muted("WHY"), Ui.Text(hint.Why, 13));
        foreach (var e in _hints.Evidence(hint))
        {
            body.Children.Add(Ui.Card(Ui.Stack(2, Ui.Text($"“{e.Quote}”", 12), e.Source.Length > 0 ? Ui.Link(e.Label, e.Source) : Ui.Muted(e.Label))).With(c => c.Padding = new Thickness(10, 6, 10, 6)));
        }

        if (hint.How.Count > 0)
        {
            body.Children.Add(Ui.Muted("HOW"));
            body.Children.Add(Ui.Text(string.Join("\n", hint.How.Select((h, i) => $"{i + 1}. {h}")), 13));
        }

        if (hint.Example is not null)
        {
            body.Children.Add(Ui.Muted("EXAMPLE"));
            body.Children.Add(Ui.Text(hint.Example, 13));
        }

        return Ui.Card(body, Ui.Info);
    }

    private async Task NavigateAsync(string url)
    {
        await _browser.EnsureCoreWebView2Async();
        _browser.Source = new Uri(url);
    }

    private void Mark(ImplementationStep step, bool done)
    {
        if (_plan is null || _folder is null)
        {
            return;
        }

        Implementations.Mark(_session, _plan, _folder, step.Id, done);
        Fill();
        Show(step);
        _ = PreviewsAsync();
    }
}
