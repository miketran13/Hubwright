using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hubwright.Runtime;

namespace Hubwright.Security;

/// <summary>API keys and tokens. The config names a secret; it never holds one.</summary>
public interface ISecretStore
{
    string? Get(string name);

    void Set(string name, string value);

    bool Remove(string name);

    IReadOnlyList<string> Names();
}

/// <summary>
/// Secrets in <c>~/.hubwright/secrets.json</c>. On Windows each value is encrypted for the current user
/// with DPAPI. Elsewhere values are stored as written, in a file only the owner may read, and the
/// store refuses to read the file if other users can. <c>HUBWRIGHT_SECRET_&lt;NAME&gt;</c> in the
/// environment overrides a stored value (CI, servers).
/// </summary>
public sealed class SecretStore(string path) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Hubwright.v1");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const string WindowsMarker = "dpapi:";

    public static SecretStore Default() => new(HubwrightHome.PathFor("secrets.json"));

    public string Path => path;

    public string? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (Environment.GetEnvironmentVariable(EnvironmentName(name)) is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        if (!Load().TryGetValue(name, out var stored))
        {
            return null;
        }

        if (!stored.StartsWith(WindowsMarker, StringComparison.Ordinal))
        {
            return stored;
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[WindowsMarker.Length..]), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null; // Written by another user or machine.
        }
    }

    public void Set(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var all = Load();
        all[name] = OperatingSystem.IsWindows()
            ? WindowsMarker + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser))
            : value;
        Save(all);
    }

    public bool Remove(string name)
    {
        var all = Load();
        var removed = all.Remove(name);
        if (removed)
        {
            Save(all);
        }

        return removed;
    }

    public IReadOnlyList<string> Names() => [.. Load().Keys.Order(StringComparer.OrdinalIgnoreCase)];

    public static string EnvironmentName(string name)
        => "HUBWRIGHT_SECRET_" + new string([.. name.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_')]);

    private Dictionary<string, string> Load()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) != 0)
        {
            throw new UnauthorizedAccessException($"{path} is readable by other users. Run: chmod 600 {path}");
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, string> all)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new SortedDictionary<string, string>(all, StringComparer.OrdinalIgnoreCase), Json));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
