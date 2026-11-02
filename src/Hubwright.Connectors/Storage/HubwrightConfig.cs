using System.Text.Json;
using System.Text.Json.Serialization;
using Hubwright.Config;
using Hubwright.Runtime;

namespace Hubwright.Storage;

/// <summary>How the admin signs in.</summary>
public enum SignInMethod
{
    /// <summary>The system browser (any OS): pick an account, sign in, close the tab.</summary>
    Browser,

    /// <summary>The Windows account broker (WAM): single sign-on with the account signed in to Windows.</summary>
    Windows,

    /// <summary>A code to enter at microsoft.com/devicelogin, from any device. For machines without a browser.</summary>
    DeviceCode,
}

/// <summary>The tenant Hubwright signs in to, with the user's own app registration.</summary>
public sealed class TenantConnection
{
    /// <summary>The tenant prefix: <c>contoso</c> for contoso.sharepoint.com. Found from Graph at sign-in when empty.</summary>
    public string? Name { get; set; }

    public SignInMethod SignIn { get; set; } = SignInMethod.Browser;

    /// <summary>Directory (tenant) id or domain, for example contoso.onmicrosoft.com.</summary>
    public string? TenantId { get; set; }

    /// <summary>The public-client app id created by tools/register-app.ps1. Never shipped in the repo.</summary>
    public string? ClientId { get; set; }

    [JsonIgnore]
    public string RootUrl => $"https://{Name}.sharepoint.com";

    [JsonIgnore]
    public string AdminUrl => $"https://{Name}-admin.sharepoint.com";
}

public sealed class SkillSourcesOptions
{
    /// <summary>A folder of organization skills that replaces built-in skills with the same id.</summary>
    public string? OrganizationDirectory { get; set; }
}

public sealed class ScanOptions
{
    /// <summary>Most sites read in detail (navigation, permissions, lists); the rest are listed only.</summary>
    public int MaxDetailedSites { get; set; } = 200;

    /// <summary>How deep permissions are sampled: 0 = site only, 1 = lists and libraries.</summary>
    public int PermissionDepth { get; set; } = 1;

    public int Parallelism { get; set; } = 4;
}

/// <summary>The desktop app's look.</summary>
public sealed class DesktopOptions
{
    /// <summary>System (follow Windows), Light or Dark.</summary>
    public string Theme { get; set; } = "System";
}

/// <summary><c>~/.hubwright/hubwright.json</c>: AI backend, tenant connection, skill folders, scan limits and the desktop's look. No secrets.</summary>
public sealed class HubwrightConfig
{
    public AiOptions Ai { get; set; } = new();

    public DesktopOptions Desktop { get; set; } = new();

    public TenantConnection Tenant { get; set; } = new();

    public SkillSourcesOptions Skills { get; set; } = new();

    public ScanOptions Scan { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string DefaultPath => HubwrightHome.PathFor("hubwright.json");

    public static HubwrightConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return new HubwrightConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<HubwrightConfig>(File.ReadAllText(path), Json) ?? new HubwrightConfig();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{path} is not valid JSON: {ex.Message}", ex);
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
