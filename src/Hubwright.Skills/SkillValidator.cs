using System.Text.RegularExpressions;
using Hubwright.Rules;

namespace Hubwright.Skills;

public enum IssueLevel { Warning, Error }

public sealed record ValidationIssue(IssueLevel Level, string SkillId, string? RuleId, string Message)
{
    public override string ToString() => $"{(Level == IssueLevel.Error ? "error" : "warn ")} {SkillId}{(RuleId is null ? "" : "/" + RuleId)}: {Message}";
}

/// <summary>
/// Checks skills before they ship. Errors fail <c>hubwright skills validate</c> and CI: unparseable
/// rules, unknown facts or parameters, duplicate ids, and documented rules without a source and an
/// exact quote. Warnings flag reviews older than a year and sources a rule uses but the skill does
/// not list.
/// </summary>
public static partial class SkillValidator
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(365);

    [GeneratedRegex(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SkillIdPattern();

    [GeneratedRegex(@"^[a-z0-9-]+\.[a-z0-9.-]+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RuleIdPattern();

    [GeneratedRegex(@"\$([A-Za-z][A-Za-z0-9_]*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ParamPattern();

    public static IReadOnlyList<ValidationIssue> Validate(SkillLibrary library, DateOnly today)
    {
        var issues = new List<ValidationIssue>();
        issues.AddRange(library.Errors.Select(e => new ValidationIssue(IssueLevel.Error, Path.GetFileName(e.Directory), null, e.Message)));
        issues.AddRange(library.Overrides.Where(o => o.Outdated).Select(o =>
            new ValidationIssue(IssueLevel.Warning, o.Id, null, $"the {o.To} copy (v{o.ToVersion}) replaces a newer {o.From} version (v{o.FromVersion}); review what changed.")));

        var ruleIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in library.Skills)
        {
            issues.AddRange(Validate(skill, today));
            foreach (var rule in skill.Rules)
            {
                if (ruleIds.TryGetValue(rule.Id, out var other))
                {
                    issues.Add(new(IssueLevel.Error, skill.Id, rule.Id, $"rule id is also used by skill {other}."));
                }

                ruleIds[rule.Id] = skill.Id;
            }
        }

        foreach (var skill in library.Skills)
        {
            foreach (var hint in skill.Hints)
            {
                foreach (var rule in hint.Rules.Where(r => !ruleIds.ContainsKey(r)))
                {
                    issues.Add(new(IssueLevel.Error, skill.Id, "hint:" + hint.Id, $"names rule {rule}, which no skill defines."));
                }
            }
        }

        // Site patterns point at templates another skill catalogues.
        var templates = library.Templates.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var patterns = library.Patterns;
        foreach (var skill in library.Skills)
        {
            foreach (var pattern in skill.Patterns.Where(p => p.Template is not null))
            {
                if (!templates.TryGetValue(pattern.Template!, out var template))
                {
                    issues.Add(new(IssueLevel.Error, skill.Id, "pattern:" + pattern.Id, $"template {pattern.Template} is not in any skill's templates.yaml."));
                }
                else if (!Templates.TemplateMatcher.SameFamily(template.Kind, pattern.Kind))
                {
                    issues.Add(new(IssueLevel.Error, skill.Id, "pattern:" + pattern.Id, $"template {template.Id} is for a different kind of site."));
                }
            }
        }

        foreach (var role in patterns.Count == 0 ? [] : new[] { "home", "hub", "communication", "team" }.Where(r => !patterns.Any(p => p.Fallback == r)))
        {
            issues.Add(new(IssueLevel.Warning, "site-design", null, $"no pattern is the fallback for {role} sites; the first pattern of the kind is used."));
        }

        return issues;
    }

    public static IReadOnlyList<ValidationIssue> Validate(Skill skill, DateOnly today)
    {
        var issues = new List<ValidationIssue>();
        void Error(string? rule, string message) => issues.Add(new(IssueLevel.Error, skill.Id, rule, message));
        void Warn(string? rule, string message) => issues.Add(new(IssueLevel.Warning, skill.Id, rule, message));

        if (!SkillIdPattern().IsMatch(skill.Id))
        {
            Error(null, "skill id must be lower-case words joined by hyphens, like external-sharing.");
        }

        if (!string.Equals(Path.GetFileName(skill.Directory), skill.Id, StringComparison.OrdinalIgnoreCase))
        {
            Warn(null, $"folder name '{Path.GetFileName(skill.Directory)}' differs from the id; keep them the same.");
        }

        if (skill.Sources.Count == 0)
        {
            Error(null, "a skill must list at least one source.");
        }

        foreach (var source in skill.Sources)
        {
            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                Error(null, $"source '{source.Url}' is not an https URL.");
            }

            if (source.Reviewed is null)
            {
                Error(null, $"source {source.Url} has no reviewed date.");
            }
            else if (today.DayNumber - source.Reviewed.Value.DayNumber > StaleAfter.TotalDays)
            {
                Warn(null, $"source {source.Url} was last reviewed {source.Reviewed:yyyy-MM-dd}; run `hubwright skills refresh {skill.Id}`.");
            }
        }

        if (string.IsNullOrWhiteSpace(skill.Guidance))
        {
            Warn(null, "SKILL.md has no guidance below the front matter; the AI has nothing to explain from.");
        }

        var listed = skill.Sources.Select(s => s.Url.TrimEnd('/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var template in skill.Templates)
        {
            var where = "template:" + template.Id;
            if (!SkillIdPattern().IsMatch(template.Id))
            {
                Error(where, "template id must be lower-case words joined by hyphens.");
            }

            if (string.IsNullOrWhiteSpace(template.Description) || string.IsNullOrWhiteSpace(template.Quote) || template.Reviewed is null)
            {
                Error(where, "a template needs Microsoft's description, a quote from its source page and a reviewed date.");
            }

            if (!listed.Contains(template.SourceUrl.TrimEnd('/')))
            {
                Warn(where, $"its url {template.SourceUrl} is not one of the skill's sources.");
            }

            if (template.Source == Templates.TemplateSource.PnpPackage && (template.Package is null || !template.Package.EndsWith(".pnp", StringComparison.OrdinalIgnoreCase)))
            {
                Error(where, "a look book package needs the https link to its .pnp file.");
            }

            if (template.SuggestFor.Count == 0)
            {
                Warn(where, "no suggestFor keywords: it will never be suggested.");
            }
        }

        foreach (var pattern in skill.Patterns)
        {
            var where = "pattern:" + pattern.Id;
            if (!SkillIdPattern().IsMatch(pattern.Id))
            {
                Error(where, "a pattern id must be lower-case words joined by hyphens.");
            }

            if (pattern.Sections.Count is 0 or > Designs.SiteDesigner.MaxSections || pattern.Sections.Any(x => !Designs.SiteDesigner.Sections.Contains(x)))
            {
                Error(where, $"sections must be 1 to {Designs.SiteDesigner.MaxSections} of: {string.Join(", ", Designs.SiteDesigner.Sections)}.");
            }

            if (pattern.Navigation.Count > Designs.SiteDesigner.MaxNavigation || pattern.Navigation.Any(l => string.IsNullOrWhiteSpace(l) || l.Length > Designs.SiteDesigner.MaxLabel))
            {
                Error(where, $"navigation is at most {Designs.SiteDesigner.MaxNavigation} labels of up to {Designs.SiteDesigner.MaxLabel} characters.");
            }

            if (!Designs.SiteDesigner.HeaderLayouts.Contains(pattern.HeaderLayout))
            {
                Error(where, $"headerLayout must be one of {string.Join(", ", Designs.SiteDesigner.HeaderLayouts)}.");
            }

            if (pattern.Kind is not (Snapshot.SiteKind.Communication or Snapshot.SiteKind.TeamGroup or Snapshot.SiteKind.TeamNoGroup))
            {
                Error(where, "kind must be communication, teamGroup or teamNoGroup.");
            }

            if (pattern.Fallback is not (null or "home" or "hub" or "communication" or "team"))
            {
                Error(where, "fallback must be home, hub, communication or team.");
            }

            if (pattern.Basis == Basis.Documented && (string.IsNullOrWhiteSpace(pattern.Source) || string.IsNullOrWhiteSpace(pattern.Quote) || pattern.Reviewed is null))
            {
                Error(where, "a documented pattern needs source, an exact quote and reviewed.");
            }

            if (pattern.Basis == Basis.Heuristic && string.IsNullOrWhiteSpace(pattern.Note))
            {
                Error(where, "a heuristic pattern needs a note saying what it is based on.");
            }

            if (pattern.Practices.Count == 0)
            {
                Error(where, "a pattern needs at least one practice: it is the explanation people see.");
            }

            if (pattern.SuggestFor.Count == 0 && pattern.Fallback is null)
            {
                Warn(where, "no suggestFor keywords and not a fallback: it will never be chosen.");
            }
        }

        foreach (var setting in skill.Baseline)
        {
            var where = "baseline:" + setting.Id;
            if (!SkillIdPattern().IsMatch(setting.Id))
            {
                Error(where, "a baseline setting id must be lower-case words joined by hyphens.");
            }

            if (setting.Targets.Count == 0 || setting.Targets.Keys.Any(k => !Enum.TryParse<Security.BaselineProfile>(k, true, out _)))
            {
                Error(where, "targets must name at least one profile: standard, strict or regulated.");
            }

            if (setting.Basis == Basis.Documented && (string.IsNullOrWhiteSpace(setting.Source) || string.IsNullOrWhiteSpace(setting.Quote) || setting.Reviewed is null))
            {
                Error(where, "a documented baseline setting needs source, an exact quote and reviewed.");
            }

            if (setting.Basis == Basis.Heuristic && string.IsNullOrWhiteSpace(setting.Note))
            {
                Error(where, "a heuristic baseline setting needs a note saying what the value is based on.");
            }

            switch (setting.Apply)
            {
                case Security.BaselineApply.Graph when !Security.SecurityBaseline.GraphProperties.Contains(setting.Property ?? "", StringComparer.Ordinal):
                    Error(where, $"Graph property {setting.Property} is not one Hubwright may change.");
                    break;
                case Security.BaselineApply.Pnp when !Implementation.PnpScripts.TenantParameters.Contains(setting.Property ?? "", StringComparer.Ordinal):
                    Error(where, $"Set-PnPTenant -{setting.Property} is not one Hubwright may set.");
                    break;
                case Security.BaselineApply.Guided when string.IsNullOrWhiteSpace(setting.GuideUrl) || setting.Instructions.Count == 0:
                    Error(where, "a guided setting needs guideUrl and instructions.");
                    break;
            }

            if (setting.Fact is not null && !Security.SecurityBaseline.Facts.Contains(setting.Fact, StringComparer.Ordinal))
            {
                Error(where, $"fact {setting.Fact} is not one the baseline can read.");
            }

            if (setting.Hint is null)
            {
                Error(where, "every baseline setting needs a hint.");
            }
        }

        foreach (var hint in skill.Hints)
        {
            var where = "hint:" + hint.Id;
            if (!SkillIdPattern().IsMatch(hint.Id))
            {
                Error(where, "hint id must be lower-case words joined by hyphens.");
            }

            if (hint.Quote is not null && (hint.Source is null || hint.Reviewed is null))
            {
                Error(where, "a hint that quotes a page needs its source and a reviewed date.");
            }

            if (hint.How.Count == 0)
            {
                Warn(where, "no how steps.");
            }
        }

        foreach (var question in skill.Questions)
        {
            if (question.Default is not null && question.Choices.Count > 0 && !question.Choices.Contains(question.Default, StringComparer.OrdinalIgnoreCase))
            {
                Error("question:" + question.Id, $"default '{question.Default}' is not one of its choices.");
            }
        }

        foreach (var rule in skill.Rules)
        {
            if (!RuleIdPattern().IsMatch(rule.Id))
            {
                Error(rule.Id, "rule id must look like area.name, lower-case (for example sharing.default-link).");
            }

            if (rule.Basis == Basis.Documented)
            {
                if (string.IsNullOrWhiteSpace(rule.Source))
                {
                    Error(rule.Id, "a documented rule needs a source URL.");
                }

                if (string.IsNullOrWhiteSpace(rule.Quote))
                {
                    Error(rule.Id, "a documented rule needs the exact quote it enforces.");
                }

                if (rule.Reviewed is null)
                {
                    Error(rule.Id, "a documented rule needs a reviewed date.");
                }
            }

            if (rule.Source is { } url && !listed.Contains(url.TrimEnd('/')))
            {
                Warn(rule.Id, $"source {url} is not in the skill's sources list.");
            }

            if (rule.Kind == RuleKind.Advisory)
            {
                continue;
            }

            Check(rule, rule.When, "when", Error);
            Check(rule, rule.Filter, "filter", Error);
            foreach (Match match in ParamPattern().Matches($"{rule.When} {rule.Filter} {rule.Message}"))
            {
                if (!rule.Params.ContainsKey(match.Groups[1].Value))
                {
                    Error(rule.Id, $"${match.Groups[1].Value} is used but not defined under params.");
                }
            }
        }

        return issues;
    }

    private static void Check(RuleDefinition rule, string? text, string field, Action<string?, string> error)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            if (field == "when")
            {
                error(rule.Id, "a check rule needs a when condition (or set kind: advisory).");
            }

            return;
        }

        try
        {
            foreach (var fact in Expression.Parse(text).Facts().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!FactCatalog.Exists(rule.Scope, fact))
                {
                    error(rule.Id, $"{field}: there is no fact '{fact}' for {rule.Scope.ToString().ToLowerInvariant()} rules (see `hubwright facts {rule.Scope.ToString().ToLowerInvariant()}`).");
                }
            }
        }
        catch (RuleException ex)
        {
            error(rule.Id, $"{field}: {ex.Message}");
        }
    }
}
