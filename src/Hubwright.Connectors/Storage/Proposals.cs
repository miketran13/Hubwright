using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hubwright.Runtime;

namespace Hubwright.Storage;

public enum ProposalKind { Blueprint, SkillUpdate, SiteDesign }

public enum ProposalStatus { Pending, Accepted, Rejected }

/// <summary>
/// Something an agent or a refresh suggests and a person decides: a new blueprint, or an edit to a
/// skill. Nothing an agent proposes takes effect until <c>hubwright proposals accept</c>.
/// </summary>
public sealed class Proposal
{
    public string Id { get; set; } = "";

    public ProposalKind Kind { get; set; }

    public string Title { get; set; } = "";

    public string Summary { get; set; } = "";

    /// <summary>The tenant a blueprint is for, or the skill id an update is for.</summary>
    public string Target { get; set; } = "";

    /// <summary>The proposed content: blueprint JSON, or the skill files (name → text).</summary>
    public Dictionary<string, string> Files { get; set; } = [];

    public string ProposedBy { get; set; } = "";

    public DateTimeOffset Created { get; set; } = DateTimeOffset.UtcNow;

    public ProposalStatus Status { get; set; }

    public string? DecidedBy { get; set; }

    public DateTimeOffset? Decided { get; set; }
}

/// <summary><c>~/.hubwright/proposals/&lt;id&gt;.json</c>.</summary>
public sealed class ProposalStore(string? directory = null)
{
    private readonly string _directory = directory ?? HubwrightHome.PathFor("proposals");

    public Proposal Add(Proposal proposal)
    {
        proposal.Id = $"{proposal.Created.UtcDateTime:yyyyMMdd-HHmmss}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant()}";
        Save(proposal);
        return proposal;
    }

    public IReadOnlyList<Proposal> All()
        => Directory.Exists(_directory)
            ? [.. Directory.GetFiles(_directory, "*.json").OrderDescending(StringComparer.Ordinal).Select(Read).OfType<Proposal>()]
            : [];

    public Proposal? Find(string id) => All().FirstOrDefault(p => p.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase));

    public void Save(Proposal proposal)
    {
        Directory.CreateDirectory(_directory);
        TenantStore.Write(Path.Combine(_directory, proposal.Id + ".json"), JsonSerializer.Serialize(proposal, HubwrightConfig.Json));
    }

    private static Proposal? Read(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Proposal>(File.ReadAllText(path), HubwrightConfig.Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>One line of the audit log.</summary>
public sealed record AuditEntry(DateTimeOffset At, string Actor, string Action, string Detail, string Previous, string Hash);

/// <summary>
/// An append-only, hash-chained log of what people decided (scans, accepted proposals, exports).
/// Each entry hashes the previous one, so an edited or deleted line breaks the chain.
/// </summary>
public sealed class AuditLog(string? path = null)
{
    private readonly string _path = path ?? HubwrightHome.PathFor("audit.jsonl");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lock Gate = new();

    public AuditEntry Append(string action, string detail, string? actor = null)
    {
        lock (Gate)
        {
            var previous = Read().LastOrDefault()?.Hash ?? "";
            var at = DateTimeOffset.UtcNow;
            actor ??= Environment.UserName;
            var hash = Hash(previous, at, actor, action, detail);
            var entry = new AuditEntry(at, actor, action, detail, previous, hash);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + "\n");
            return entry;
        }
    }

    public IReadOnlyList<AuditEntry> Read()
        => File.Exists(_path)
            ? [.. File.ReadAllLines(_path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<AuditEntry>(l, Json)).OfType<AuditEntry>()]
            : [];

    /// <summary>The index of the first entry whose hash does not match, or -1 when the chain is intact.</summary>
    public int Verify()
    {
        var previous = "";
        var entries = Read();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Previous != previous || e.Hash != Hash(e.Previous, e.At, e.Actor, e.Action, e.Detail))
            {
                return i;
            }

            previous = e.Hash;
        }

        return -1;
    }

    private static string Hash(string previous, DateTimeOffset at, string actor, string action, string detail)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{previous}\n{at:O}\n{actor}\n{action}\n{detail}"))).ToLowerInvariant();
}
