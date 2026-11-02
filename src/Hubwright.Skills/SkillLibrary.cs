using Hubwright.Rules;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Hubwright.Skills;

/// <summary>A skill that replaced another with the same id from an earlier layer.</summary>
public sealed record SkillOverride(string Id, SkillLayer From, string FromVersion, SkillLayer To, string ToVersion)
{
    /// <summary>True when the replacement is older than what it replaces: worth a review.</summary>
    public bool Outdated => System.Version.TryParse(ToVersion, out var to) && System.Version.TryParse(FromVersion, out var from) && to < from;
}

/// <summary>
/// The skills in effect: built-in first, then the organization's folder, then the user's. A later
/// layer replaces a skill with the same id, so an organization can tune or fork any built-in skill
/// without editing it, and a newer built-in never silently overwrites the organization's version.
/// </summary>
public sealed class SkillLibrary
{
    private readonly Dictionary<string, Skill> _skills;

    private SkillLibrary(Dictionary<string, Skill> skills, List<SkillLoadError> errors, List<SkillOverride> overrides)
    {
        _skills = skills;
        Errors = errors;
        Overrides = overrides;
    }

    public IReadOnlyList<Skill> Skills => [.. _skills.Values.OrderBy(s => s.Id, StringComparer.Ordinal)];

    public IReadOnlyList<SkillLoadError> Errors { get; }

    public IReadOnlyList<SkillOverride> Overrides { get; }

    public IReadOnlyList<RuleDefinition> Rules => [.. Skills.SelectMany(s => s.Rules)];

    public IReadOnlyDictionary<string, string> Versions => Skills.ToDictionary(s => s.Id, s => s.Version, StringComparer.OrdinalIgnoreCase);

    public Skill? Find(string id) => _skills.GetValueOrDefault(id);

    /// <summary>Every catalogued template; a later layer's template replaces one with the same id.</summary>
    public IReadOnlyList<Designs.SitePattern> Patterns => [.. Skills.SelectMany(s => s.Patterns).GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())];

    public IReadOnlyList<Templates.SiteTemplate> Templates => [.. Skills.SelectMany(s => s.Templates).GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())];

    /// <summary>Every hint by id; a later layer's hint replaces one with the same id.</summary>
    /// <summary>The security baseline (a later layer replaces a setting by id).</summary>
    public IReadOnlyList<Security.BaselineSetting> Baseline => [.. Skills.SelectMany(s => s.Baseline).GroupBy(b => b.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.Last())];

    public IReadOnlyDictionary<string, Hints.Hint> Hints => Skills.SelectMany(s => s.Hints).GroupBy(h => h.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Hints plus every rule, to explain anything a proposal or plan shows.</summary>
    public Hints.HintBook HintBook() => new(Hints, Rules.GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase));

    /// <summary>Every skill question, with the skill that asks it.</summary>
    public IReadOnlyList<(Skill Skill, SkillQuestion Question)> Questions => [.. Skills.SelectMany(s => s.Questions.Select(q => (s, q)))];

    public static SkillLibrary Load(IEnumerable<(string Directory, SkillLayer Layer)> layers)
    {
        var skills = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<SkillLoadError>();
        var overrides = new List<SkillOverride>();
        foreach (var (directory, layer) in layers)
        {
            var (loaded, failed) = SkillLoader.LoadAll(directory, layer);
            errors.AddRange(failed);
            foreach (var skill in loaded)
            {
                if (skills.TryGetValue(skill.Id, out var existing))
                {
                    overrides.Add(new SkillOverride(skill.Id, existing.Layer, existing.Version, skill.Layer, skill.Version));
                }

                skills[skill.Id] = skill;
            }
        }

        return new SkillLibrary(skills, errors, overrides);
    }

    /// <summary>
    /// The built-in skills folder: <c>skills/</c> next to the executable (published builds), else the
    /// repository's <c>skills/</c> found by walking up from the executable (development builds).
    /// </summary>
    public static string? BuiltInDirectory()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "skills");
        if (System.IO.Directory.Exists(beside))
        {
            return beside;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "skills");
            if (System.IO.Directory.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "Hubwright.slnx")))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>A tenant's rule changes, read from <c>overrides.yaml</c>.</summary>
public static class RuleOverrides
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).IgnoreUnmatchedProperties().Build();

    /// <summary>
    /// <code>
    /// rules:
    ///   lifecycle.inactive-sites:
    ///     params: { inactiveDays: 365 }
    ///   hubs.single-hub:
    ///     disabled: true
    /// </code>
    /// </summary>
    public static IReadOnlyDictionary<string, RuleOverride> Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return new Dictionary<string, RuleOverride>();
        }

        var file = Yaml.Deserialize<OverridesDto>(File.ReadAllText(path)) ?? new OverridesDto();
        return file.Rules.ToDictionary(
            r => r.Key,
            r => new RuleOverride
            {
                Disabled = r.Value.Disabled,
                Severity = Enum.TryParse<Severity>(r.Value.Severity, ignoreCase: true, out var s) ? s : null,
                Params = r.Value.Params.ToDictionary(p => p.Key, p => SkillLoader.ParseScalar(p.Value), StringComparer.OrdinalIgnoreCase),
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private sealed class OverridesDto
    {
        public Dictionary<string, OverrideDto> Rules { get; set; } = [];
    }

    private sealed class OverrideDto
    {
        public bool Disabled { get; set; }

        public string? Severity { get; set; }

        public Dictionary<string, object?> Params { get; set; } = [];
    }
}
