using System.Globalization;
using Hubwright.Rules;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Hubwright.Skills;

/// <summary>A skill folder that could not be read, with the reason, so one bad skill never hides the rest.</summary>
public sealed record SkillLoadError(string Directory, string Message);

/// <summary>Reads skill folders: SKILL.md front matter and guidance, rules.yaml and questions.yaml.</summary>
public static class SkillLoader
{
    public const string SkillFile = "SKILL.md";
    public const string RulesFile = "rules.yaml";
    public const string QuestionsFile = "questions.yaml";
    public const string TemplatesFile = "templates.yaml";
    public const string HintsFile = "hints.yaml";
    public const string PatternsFile = "patterns.yaml";
    public const string BaselineFile = "baseline.yaml";

    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Every skill folder directly under <paramref name="root"/> (folders that contain SKILL.md).</summary>
    public static (IReadOnlyList<Skill> Skills, IReadOnlyList<SkillLoadError> Errors) LoadAll(string root, SkillLayer layer)
    {
        var skills = new List<Skill>();
        var errors = new List<SkillLoadError>();
        if (!System.IO.Directory.Exists(root))
        {
            return (skills, errors);
        }

        foreach (var directory in System.IO.Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(directory, SkillFile)))
            {
                continue;
            }

            try
            {
                skills.Add(Load(directory, layer));
            }
            catch (Exception ex) when (ex is YamlException or FormatException or InvalidDataException or IOException)
            {
                errors.Add(new SkillLoadError(directory, ex is YamlException y ? $"YAML error at line {y.Start.Line}: {y.InnerException?.Message ?? y.Message}" : ex.Message));
            }
        }

        return (skills, errors);
    }

    public static Skill Load(string directory, SkillLayer layer)
    {
        var (frontMatter, body) = SplitFrontMatter(File.ReadAllText(Path.Combine(directory, SkillFile)));
        var manifest = Yaml.Deserialize<ManifestDto>(frontMatter) ?? throw new InvalidDataException($"{SkillFile} has no front matter.");
        var id = Required(manifest.Id, "id");
        var category = string.IsNullOrWhiteSpace(manifest.Category) ? id : manifest.Category.Trim();
        var version = Required(manifest.Version, "version");
        if (!System.Version.TryParse(version, out _))
        {
            throw new InvalidDataException($"{id}: version '{version}' is not a version number like 1.0.0.");
        }

        var rules = new List<RuleDefinition>();
        var rulesPath = Path.Combine(directory, RulesFile);
        if (File.Exists(rulesPath))
        {
            var file = Yaml.Deserialize<RulesFileDto>(File.ReadAllText(rulesPath)) ?? new RulesFileDto();
            foreach (var dto in file.Rules)
            {
                var rule = ToRule(dto, id);
                rule.SkillId = id;
                rule.SkillVersion = version;
                rule.Category = category;
                rules.Add(rule);
            }
        }

        var questions = new List<SkillQuestion>();
        var questionsPath = Path.Combine(directory, QuestionsFile);
        if (File.Exists(questionsPath))
        {
            questions.AddRange(Yaml.Deserialize<QuestionsFileDto>(File.ReadAllText(questionsPath))?.Questions ?? []);
        }

        var templates = new List<Templates.SiteTemplate>();
        var templatesPath = Path.Combine(directory, TemplatesFile);
        if (File.Exists(templatesPath))
        {
            foreach (var dto in Yaml.Deserialize<TemplatesFileDto>(File.ReadAllText(templatesPath))?.Templates ?? [])
            {
                templates.Add(ToTemplate(dto, id));
            }
        }

        var patterns = new List<Designs.SitePattern>();
        var patternsPath = Path.Combine(directory, PatternsFile);
        if (File.Exists(patternsPath))
        {
            foreach (var dto in Yaml.Deserialize<PatternsFileDto>(File.ReadAllText(patternsPath))?.Patterns ?? [])
            {
                var patternId = Required(dto.Id, $"patterns[].id in {id}");
                patterns.Add(new Designs.SitePattern
                {
                    Id = patternId,
                    Name = Required(dto.Name, $"{patternId}.name"),
                    Purpose = Required(dto.Purpose, $"{patternId}.purpose"),
                    Kind = ParseEnum(dto.Kind, Snapshot.SiteKind.Communication, $"{patternId}.kind"),
                    SuggestFor = dto.SuggestFor,
                    Fallback = dto.Fallback,
                    Template = dto.Template,
                    Sections = dto.Sections,
                    Navigation = dto.Navigation,
                    HeaderLayout = dto.HeaderLayout ?? "standard",
                    Audience = dto.Audience ?? "",
                    Practices = dto.Practices,
                    Basis = ParseEnum(dto.Basis, Basis.Heuristic, $"{patternId}.basis"),
                    Note = dto.Note,
                    Source = dto.Source,
                    Quote = dto.Quote?.Trim(),
                    Reviewed = ParseDate(dto.Reviewed, $"{patternId}.reviewed"),
                    SkillId = id,
                });
            }
        }

        var hints = new List<Hints.Hint>();
        var hintsPath = Path.Combine(directory, HintsFile);
        if (File.Exists(hintsPath))
        {
            foreach (var dto in Yaml.Deserialize<HintsFileDto>(File.ReadAllText(hintsPath))?.Hints ?? [])
            {
                var hintId = Required(dto.Id, $"hints[].id in {id}");
                hints.Add(new Hints.Hint
                {
                    Id = hintId,
                    Title = Required(dto.Title, $"{hintId}.title"),
                    What = Required(dto.What, $"{hintId}.what"),
                    Why = Required(dto.Why, $"{hintId}.why"),
                    How = dto.How,
                    Example = dto.Example?.Trim(),
                    Rules = dto.Rules,
                    Source = dto.Source,
                    Quote = dto.Quote?.Trim(),
                    Reviewed = ParseDate(dto.Reviewed, $"{hintId}.reviewed"),
                    SkillId = id,
                });
            }
        }

        var baseline = new List<Security.BaselineSetting>();
        var baselinePath = Path.Combine(directory, BaselineFile);
        if (File.Exists(baselinePath))
        {
            foreach (var dto in Yaml.Deserialize<BaselineFileDto>(File.ReadAllText(baselinePath))?.Settings ?? [])
            {
                var settingId = Required(dto.Id, $"settings[].id in {id}");
                baseline.Add(new Security.BaselineSetting
                {
                    Id = settingId,
                    Title = Required(dto.Title, $"{settingId}.title"),
                    Area = Required(dto.Area, $"{settingId}.area"),
                    Apply = ParseEnum(dto.Apply, Security.BaselineApply.Guided, $"{settingId}.apply"),
                    Property = dto.Property,
                    ValueType = dto.Type ?? "string",
                    Targets = new Dictionary<string, string>(dto.Targets.Where(t => t.Value is not null).ToDictionary(t => t.Key, t => t.Value!.Trim()), StringComparer.OrdinalIgnoreCase),
                    Fact = dto.Fact,
                    Why = Required(dto.Why, $"{settingId}.why"),
                    Basis = ParseEnum(dto.Basis, Basis.Documented, $"{settingId}.basis"),
                    Source = dto.Source,
                    Quote = dto.Quote?.Trim(),
                    Reviewed = ParseDate(dto.Reviewed, $"{settingId}.reviewed"),
                    Note = dto.Note,
                    GuideUrl = dto.GuideUrl,
                    Instructions = dto.Instructions,
                    Rules = dto.Rules,
                    Hint = dto.Hint,
                    SkillId = id,
                });
            }
        }

        return new Skill
        {
            Id = id,
            Title = Required(manifest.Title, "title"),
            Version = version,
            Category = category,
            Description = manifest.Description,
            AppliesTo = ParseEnum(manifest.AppliesTo, AppliesTo.Both, "appliesTo"),
            Owner = manifest.Owner,
            Sources = [.. manifest.Sources.Select(s => new SkillSource { Url = s.Url ?? "", Title = s.Title, Reviewed = ParseDate(s.Reviewed, "reviewed"), Note = s.Note })],
            Guidance = body.Trim(),
            Rules = rules,
            Questions = questions,
            Templates = templates,
            Patterns = patterns,
            Hints = hints,
            Baseline = baseline,
            Directory = directory,
            Layer = layer,
        };
    }

    internal static (string FrontMatter, string Body) SplitFrontMatter(string text)
    {
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿');
        if (!text.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{SkillFile} must start with a --- front matter block.");
        }

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidDataException($"{SkillFile} front matter is not closed with ---.");
        }

        var bodyStart = text.IndexOf('\n', end + 4);
        return (text[4..end], bodyStart < 0 ? "" : text[(bodyStart + 1)..]);
    }

    private static RuleDefinition ToRule(RuleDto dto, string skillId)
    {
        var id = Required(dto.Id, $"rules[].id in {skillId}");
        var kind = ParseEnum(dto.Kind, string.IsNullOrWhiteSpace(dto.When) ? RuleKind.Advisory : RuleKind.Check, $"{id}.kind");
        return new RuleDefinition
        {
            Id = id,
            Title = Required(dto.Title, $"{id}.title"),
            Kind = kind,
            Scope = ParseEnum(dto.Scope, RuleScope.Tenant, $"{id}.scope"),
            When = dto.When?.Trim(),
            Filter = dto.Filter?.Trim(),
            Severity = ParseEnum(dto.Severity, Severity.Medium, $"{id}.severity"),
            Weight = dto.Weight,
            Basis = ParseEnum(dto.Basis, Basis.Heuristic, $"{id}.basis"),
            AppliesTo = ParseEnum(dto.AppliesTo, AppliesTo.Both, $"{id}.appliesTo"),
            Source = dto.Source,
            Quote = dto.Quote?.Trim(),
            Reviewed = ParseDate(dto.Reviewed, $"{id}.reviewed"),
            Message = dto.Message?.Trim(),
            Fix = dto.Fix?.Trim(),
            Params = dto.Params.ToDictionary(p => p.Key, p => ParseScalar(p.Value), StringComparer.OrdinalIgnoreCase),
        };
    }

    private static Templates.SiteTemplate ToTemplate(TemplateDto dto, string skillId)
    {
        var id = Required(dto.Id, $"templates[].id in {skillId}");
        return new Templates.SiteTemplate
        {
            Id = id,
            Name = Required(dto.Name, $"{id}.name"),
            Source = ParseEnum(dto.Source, Templates.TemplateSource.Microsoft, $"{id}.source"),
            Kind = ParseEnum(dto.Kind, Snapshot.SiteKind.Communication, $"{id}.kind"),
            Category = dto.Category,
            Description = dto.Description?.Trim() ?? "",
            SourceUrl = Required(dto.Url, $"{id}.url"),
            Quote = dto.Quote?.Trim(),
            Reviewed = ParseDate(dto.Reviewed, $"{id}.reviewed"),
            SuggestFor = dto.SuggestFor,
            WebTemplate = dto.WebTemplate,
            SiteDesignId = dto.SiteDesignId,
            PnpTemplate = dto.PnpTemplate,
            Package = dto.Package,
            Parameters = dto.Parameters,
            Prerequisites = dto.Prerequisites,
            Sections = dto.Sections,
        };
    }

    internal static object? ParseScalar(object? value) => value switch
    {
        null => null,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        string s when bool.TryParse(s, out var b) => b,
        string s when s is "~" or "null" => null,
        IEnumerable<object> list => list.Select(ParseScalar).ToList(),
        _ => value,
    };

    private static string Required(string? value, string field)
        => string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException($"Missing '{field}'.") : value.Trim();

    private static T ParseEnum<T>(string? value, T fallback, string field)
        where T : struct, Enum
        => string.IsNullOrWhiteSpace(value) ? fallback
            : Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed) ? parsed
            : throw new InvalidDataException($"'{field}' is '{value}'; expected one of {string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");

    private static DateOnly? ParseDate(string? value, string field)
        => string.IsNullOrWhiteSpace(value) ? null
            : DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
            : throw new InvalidDataException($"'{field}' is '{value}'; use yyyy-mm-dd.");

    private sealed class ManifestDto
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? Version { get; set; }

        public string? Category { get; set; }

        public string? Description { get; set; }

        public string? AppliesTo { get; set; }

        public string? Owner { get; set; }

        public List<SourceDto> Sources { get; set; } = [];
    }

    private sealed class SourceDto
    {
        public string? Url { get; set; }

        public string? Title { get; set; }

        public string? Reviewed { get; set; }

        public string? Note { get; set; }
    }

    private sealed class RulesFileDto
    {
        public List<RuleDto> Rules { get; set; } = [];
    }

    private sealed class RuleDto
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? Kind { get; set; }

        public string? Scope { get; set; }

        public string? When { get; set; }

        public string? Filter { get; set; }

        public string? Severity { get; set; }

        public double? Weight { get; set; }

        public string? Basis { get; set; }

        public string? AppliesTo { get; set; }

        public string? Source { get; set; }

        public string? Quote { get; set; }

        public string? Reviewed { get; set; }

        public string? Message { get; set; }

        public string? Fix { get; set; }

        public Dictionary<string, object?> Params { get; set; } = [];
    }

    private sealed class TemplatesFileDto
    {
        public List<TemplateDto> Templates { get; set; } = [];
    }

    private sealed class TemplateDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Source { get; set; }

        public string? Kind { get; set; }

        public string? Category { get; set; }

        public string? Description { get; set; }

        public string? Url { get; set; }

        public string? Quote { get; set; }

        public string? Reviewed { get; set; }

        public List<string> SuggestFor { get; set; } = [];

        public string? WebTemplate { get; set; }

        public string? SiteDesignId { get; set; }

        public string? PnpTemplate { get; set; }

        public string? Package { get; set; }

        public List<string> Parameters { get; set; } = [];

        public List<string> Prerequisites { get; set; } = [];

        public List<string> Sections { get; set; } = [];
    }

    private sealed class BaselineFileDto
    {
        public List<BaselineDto> Settings { get; set; } = [];
    }

    private sealed class BaselineDto
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? Area { get; set; }

        public string? Apply { get; set; }

        public string? Property { get; set; }

        public string? Type { get; set; }

        public Dictionary<string, string?> Targets { get; set; } = [];

        public string? Fact { get; set; }

        public string? Why { get; set; }

        public string? Basis { get; set; }

        public string? Source { get; set; }

        public string? Quote { get; set; }

        public string? Reviewed { get; set; }

        public string? Note { get; set; }

        public string? GuideUrl { get; set; }

        public List<string> Instructions { get; set; } = [];

        public List<string> Rules { get; set; } = [];

        public string? Hint { get; set; }
    }

    private sealed class PatternsFileDto
    {
        public List<PatternDto> Patterns { get; set; } = [];
    }

    private sealed class PatternDto
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public string? Purpose { get; set; }

        public string? Kind { get; set; }

        public List<string> SuggestFor { get; set; } = [];

        public string? Fallback { get; set; }

        public string? Template { get; set; }

        public List<string> Sections { get; set; } = [];

        public List<string> Navigation { get; set; } = [];

        public string? HeaderLayout { get; set; }

        public string? Audience { get; set; }

        public List<string> Practices { get; set; } = [];

        public string? Basis { get; set; }

        public string? Note { get; set; }

        public string? Source { get; set; }

        public string? Quote { get; set; }

        public string? Reviewed { get; set; }
    }

    private sealed class HintsFileDto
    {
        public List<HintDto> Hints { get; set; } = [];
    }

    private sealed class HintDto
    {
        public string? Id { get; set; }

        public string? Title { get; set; }

        public string? What { get; set; }

        public string? Why { get; set; }

        public List<string> How { get; set; } = [];

        public string? Example { get; set; }

        public List<string> Rules { get; set; } = [];

        public string? Source { get; set; }

        public string? Quote { get; set; }

        public string? Reviewed { get; set; }
    }

    private sealed class QuestionsFileDto
    {
        public List<SkillQuestion> Questions { get; set; } = [];
    }
}
