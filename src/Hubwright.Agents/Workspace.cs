using Hubwright.Blueprints;
using Hubwright.Rules;
using Hubwright.Scoring;
using Hubwright.Skills;
using Hubwright.Snapshot;
using Hubwright.Storage;

namespace Hubwright.Agents;

/// <summary>
/// What agents and tools work over: one tenant's latest snapshot and blueprint, the skills in effect,
/// and the tenant's rule overrides. Everything is read from disk. Agents never call the tenant.
/// </summary>
public sealed class Workspace(TenantStore store, SkillLibrary skills)
{
    private Assessment? _assessment;
    private TenantSnapshot? _snapshot;
    private bool _snapshotLoaded;

    public TenantStore Store => store;

    public SkillLibrary Skills => skills;

    public IReadOnlyDictionary<string, RuleOverride> Overrides => RuleOverrides.Load(store.OverridesPath);

    public TenantSnapshot? Snapshot
    {
        get
        {
            if (!_snapshotLoaded)
            {
                _snapshot = store.Latest();
                _snapshotLoaded = true;
            }

            return _snapshot;
        }
    }

    /// <summary>Uses this snapshot instead of the latest on disk (an import, a test, a blueprint check).</summary>
    public void Use(TenantSnapshot snapshot)
    {
        _snapshot = snapshot;
        _snapshotLoaded = true;
        _assessment = null;
    }

    public Assessment? Assessment => _assessment ??= Snapshot is { } s ? Assess(s) : null;

    public Assessment Assess(TenantSnapshot snapshot) => Assessment.Run(snapshot, skills.Rules, skills.Versions, Overrides);

    public Blueprint? Blueprint => store.LatestBlueprint();

    public Assessment RequireAssessment()
        => Assessment ?? throw new InvalidOperationException($"No snapshot for tenant '{store.Tenant}' yet. Run `hubwright scan`, `hubwright import <file>` or `hubwright sample`.");
}
