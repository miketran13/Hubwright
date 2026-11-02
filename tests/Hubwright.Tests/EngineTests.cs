using Microsoft.Extensions.AI;
using Hubwright.Agents;
using Hubwright.Engine;
using Hubwright.Storage;

namespace Hubwright.Tests;

public class EngineTests
{
    [Fact]
    public void Audit_log_chain_detects_tampering()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("hubwright-audit").FullName, "audit.jsonl");
        var log = new AuditLog(path);
        log.Append("scan", "one", "tester");
        log.Append("export", "two", "tester");
        Assert.Equal(-1, log.Verify());
        File.WriteAllText(path, File.ReadAllText(path).Replace("two", "TWO", StringComparison.Ordinal));
        Assert.Equal(1, log.Verify());
    }

    [Fact]
    public async Task Tools_answer_from_the_snapshot_and_propose_without_applying()
    {
        var home = Directory.CreateTempSubdirectory("hubwright-home").FullName;
        var store = new TenantStore("fabrikam", home);
        store.SaveSnapshot(SampleTenants.Fabrikam());
        var proposals = new ProposalStore(Path.Combine(home, "proposals"));
        var workspace = new Workspace(store, SkillTests.BuiltIn());
        var tools = new ToolCatalog(workspace, proposals, Roles.Architect).All().ToDictionary(t => t.Name);

        var summary = (await tools["get_snapshot_summary"].InvokeAsync(new AIFunctionArguments()))?.ToString();
        Assert.Contains("Fabrikam", summary, StringComparison.Ordinal);

        var ownerless = (await tools["query_sites"].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["where"] = "ownerCount == 0" })))?.ToString();
        Assert.Contains("All Company", ownerless, StringComparison.Ordinal);

        var blueprint = Blueprints.BlueprintGenerator.Generate(new Blueprints.IntranetAnswers());
        var filed = (await tools["propose_blueprint"].InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["blueprintJson"] = blueprint.ToJson(), ["summary"] = "test" })))?.ToString();
        Assert.Contains("Filed proposal", filed, StringComparison.Ordinal);
        Assert.Single(proposals.All());
        Assert.Empty(store.Blueprints()); // nothing applied
    }

    [Fact]
    public void Provisioning_script_only_creates_and_avoids_reserved_variables()
    {
        var script = Exporters.ProvisioningScript(Blueprints.BlueprintGenerator.Generate(new Blueprints.IntranetAnswers { ExternalCollaboration = true }));
        Assert.Contains("Register-PnPHubSite", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-PnP", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$home ", script, StringComparison.OrdinalIgnoreCase);
    }
}
