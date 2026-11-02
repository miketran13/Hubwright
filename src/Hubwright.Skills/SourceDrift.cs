using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Hubwright.Rules;

namespace Hubwright.Skills;

/// <summary>Fetches a source page as text. The real one is HTTP (Connectors); tests pass a fake.</summary>
public interface ISourceFetcher
{
    /// <summary>The page's text, or null when it cannot be reached (with the reason).</summary>
    Task<(string? Text, string? Error)> FetchAsync(Uri url, CancellationToken cancellationToken);
}

public enum DriftStatus { QuoteFound, QuoteMissing, Unreachable, NoQuote }

/// <summary>What a refresh found for one documented rule.</summary>
public sealed record DriftResult(string SkillId, string RuleId, string Url, DriftStatus Status, string? Detail = null);

/// <summary>
/// Checks that every documented rule's quote still appears on its source page. Microsoft's docs
/// change; when a quote disappears the rule may be out of date, and <c>skills refresh</c> turns the
/// result into a proposal for a person to review. Nothing here edits a skill.
/// </summary>
public static partial class SourceDrift
{
    public static async Task<IReadOnlyList<DriftResult>> CheckAsync(IEnumerable<Skill> skills, ISourceFetcher fetcher, CancellationToken cancellationToken)
    {
        var pages = new Dictionary<string, (string? Text, string? Error)>(StringComparer.OrdinalIgnoreCase);
        var results = new List<DriftResult>();
        foreach (var skill in skills)
        {
            foreach (var rule in skill.Rules.Where(r => r.Basis == Basis.Documented && r.Source is not null))
            {
                if (string.IsNullOrWhiteSpace(rule.Quote))
                {
                    results.Add(new(skill.Id, rule.Id, rule.Source!, DriftStatus.NoQuote));
                    continue;
                }

                if (!pages.TryGetValue(rule.Source!, out var page))
                {
                    page = Uri.TryCreate(rule.Source, UriKind.Absolute, out var uri)
                        ? await fetcher.FetchAsync(uri, cancellationToken).ConfigureAwait(false)
                        : (null, "not a URL");
                    pages[rule.Source!] = page;
                }

                results.Add(page.Text is null
                    ? new(skill.Id, rule.Id, rule.Source!, DriftStatus.Unreachable, page.Error)
                    : Contains(page.Text, rule.Quote)
                        ? new(skill.Id, rule.Id, rule.Source!, DriftStatus.QuoteFound)
                        : new(skill.Id, rule.Id, rule.Source!, DriftStatus.QuoteMissing, $"\"{rule.Quote}\""));
            }

            // Catalogued templates are checked the same way: Microsoft renames and retires templates too.
            foreach (var template in skill.Templates.Where(t => !string.IsNullOrWhiteSpace(t.Quote)).Concat(skill.Hints.Where(h => h.Quote is not null && h.Source is not null).Select(h => new Templates.SiteTemplate { Id = "hint-" + h.Id, SourceUrl = h.Source!, Quote = h.Quote }))
                .Concat(skill.Baseline.Where(b => b.Quote is not null && b.Source is not null).Select(b => new Templates.SiteTemplate { Id = "baseline-" + b.Id, SourceUrl = b.Source!, Quote = b.Quote }))
                .Concat(skill.Patterns.Where(p => p.Quote is not null && p.Source is not null).Select(p => new Templates.SiteTemplate { Id = "pattern-" + p.Id, SourceUrl = p.Source!, Quote = p.Quote })))
            {
                if (!pages.TryGetValue(template.SourceUrl, out var page))
                {
                    page = Uri.TryCreate(template.SourceUrl, UriKind.Absolute, out var uri)
                        ? await fetcher.FetchAsync(uri, cancellationToken).ConfigureAwait(false)
                        : (null, "not a URL");
                    pages[template.SourceUrl] = page;
                }

                var id = "template:" + template.Id;
                results.Add(page.Text is null
                    ? new(skill.Id, id, template.SourceUrl, DriftStatus.Unreachable, page.Error)
                    : Contains(page.Text, template.Quote!)
                        ? new(skill.Id, id, template.SourceUrl, DriftStatus.QuoteFound)
                        : new(skill.Id, id, template.SourceUrl, DriftStatus.QuoteMissing, $"\"{template.Quote}\""));
            }
        }

        return results;
    }

    /// <summary>Whether the quote appears in the page, ignoring markup, case, whitespace, and quote and dash styles.</summary>
    public static bool Contains(string page, string quote) => Normalize(page).Contains(Normalize(quote), StringComparison.Ordinal);

    [GeneratedRegex("<(script|style)[^>]*>.*?</\\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex ScriptOrStyle();

    // Inline tags vanish so "the <a>app bar</a>." stays "the app bar."; block tags become a space.
    [GeneratedRegex(@"</?(a|b|strong|em|i|code|span|sup|sub|abbr|mark|u)(\s[^>]*)?>",RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex InlineTag();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[*_`]|\[|\]\([^)]*\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex MarkdownMarks();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Spaces();

    public static string Normalize(string text)
    {
        text = ScriptOrStyle().Replace(text, " ");
        text = InlineTag().Replace(text, "");
        text = Tag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = MarkdownMarks().Replace(text, "");
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '‘' or '’' or 'ʼ' => '\'',
                '“' or '”' => '"',
                '–' or '—' or '−' => '-',
                ' ' => ' ',
                _ => char.ToLowerInvariant(c),
            });
        }

        return Spaces().Replace(sb.ToString(), " ").Trim();
    }
}

/// <summary>Writes a new skill folder from a template, ready to edit.</summary>
public static class SkillScaffold
{
    public static string Create(string root, string id, string title, DateOnly today)
    {
        var directory = Path.Combine(root, id);
        if (Directory.Exists(directory))
        {
            throw new IOException($"{directory} already exists.");
        }

        Directory.CreateDirectory(directory);
        var reviewed = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(directory, SkillLoader.SkillFile), $"""
            ---
            id: {id}
            title: {title}
            version: 0.1.0
            category: {id}
            appliesTo: both
            owner: your-name-or-team
            description: One sentence on what this skill checks and why it matters.
            sources:
              - url: https://learn.microsoft.com/en-us/sharepoint/
                title: The page this skill is based on
                reviewed: {reviewed}
            ---

            # {title}

            Write the best practice here, in plain language. The AI reads this when it explains a
            finding, so say *why* it matters and *how* to fix it. Link to the sources above.
            """);
        File.WriteAllText(Path.Combine(directory, SkillLoader.RulesFile), $$"""
            # Rules for {{id}}. Facts: `hubwright facts tenant|site|hub`. Language: docs/rules-dsl.md.
            rules:
              - id: {{id}}.example
                title: Example check, replace me
                scope: site                # tenant | site | hub
                when: ownerCount < $minOwners
                params: { minOwners: 2 }
                severity: medium           # info | low | medium | high | critical
                basis: heuristic           # documented needs source + quote + reviewed
                message: "{title} has {ownerCount} owner(s); aim for at least {$minOwners}."
                fix: Add a second owner in Site settings > Site permissions.
            """);
        return directory;
    }
}
