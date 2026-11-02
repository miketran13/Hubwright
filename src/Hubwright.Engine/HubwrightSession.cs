using Hubwright.Agents;
using Hubwright.Auth;
using Hubwright.Chat;
using Hubwright.Collectors;
using Hubwright.Http;
using Hubwright.Runtime;
using Hubwright.Skills;
using Hubwright.Storage;

namespace Hubwright.Engine;

/// <summary>
/// Everything one command or the desktop app needs, built once from <c>~/.hubwright</c>: config,
/// skills (built-in, organization and user layers), the tenant's store and workspace, and the agents.
/// </summary>
public sealed class HubwrightSession
{
    private HubwrightSession(HubwrightConfig config, SkillLibrary skills, string tenant, string? executable)
    {
        Config = config;
        Skills = skills;
        Store = new TenantStore(tenant);
        Workspace = new Workspace(Store, skills);
        Proposals = new ProposalStore();
        Audit = new AuditLog();
        Models = new ChatClientFactory(config.Ai);
        Runner = new AgentRunner(Models, role => new ToolCatalog(Workspace, Proposals, role), executable is null ? null : new McpCommand(executable, Store.Tenant));
    }

    public HubwrightConfig Config { get; }

    public SkillLibrary Skills { get; }

    public TenantStore Store { get; }

    public Workspace Workspace { get; }

    public ProposalStore Proposals { get; }

    public AuditLog Audit { get; }

    public ChatClientFactory Models { get; }

    public AgentRunner Runner { get; }

    /// <summary>The user's own skills folder; <c>skills new</c> writes here and accepted updates land here.</summary>
    public static string UserSkillsDirectory => Directory.CreateDirectory(HubwrightHome.PathFor("skills")).FullName;

    /// <param name="tenant">The tenant to work on; defaults to the connected tenant, else "sample".</param>
    /// <param name="executable">This program, so CLI backends can start <c>hubwright mcp</c>; null disables tools for them.</param>
    public static HubwrightSession Open(string? tenant = null, string? executable = null)
    {
        var config = HubwrightConfig.Load();
        var skills = LoadSkills(config);
        return new HubwrightSession(config, skills, tenant ?? config.Tenant.Name ?? "sample", executable);
    }

    public static SkillLibrary LoadSkills(HubwrightConfig config)
    {
        var layers = new List<(string, SkillLayer)>();
        if (SkillLibrary.BuiltInDirectory() is { } builtIn)
        {
            layers.Add((builtIn, SkillLayer.BuiltIn));
        }

        if (!string.IsNullOrWhiteSpace(config.Skills.OrganizationDirectory))
        {
            layers.Add((Environment.ExpandEnvironmentVariables(config.Skills.OrganizationDirectory), SkillLayer.Organization));
        }

        layers.Add((UserSkillsDirectory, SkillLayer.User));
        return SkillLibrary.Load(layers);
    }

    public TenantAuth Auth(Func<IntPtr>? window = null) => new(Config.Tenant, window);

    public TenantCollector Collector(TenantAuth auth)
        => new(new TenantHttp(auth.TokenForAsync), Config.Tenant, Config.Scan);
}
