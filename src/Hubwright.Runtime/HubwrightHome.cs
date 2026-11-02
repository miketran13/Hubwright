namespace Hubwright.Runtime;

/// <summary>
/// The per-user Hubwright directory: <c>~/.hubwright</c>, or <c>HUBWRIGHT_HOME</c> when set (tests).
/// Holds the config, the token cache, runs, proposals and the audit log. Never inside a repository.
/// </summary>
public static class HubwrightHome
{
    public static string Directory
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("HUBWRIGHT_HOME");
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".hubwright")
                : configured;

            System.IO.Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string PathFor(params string[] parts) => Path.Combine([Directory, .. parts]);

    public static string RunsDirectory => System.IO.Directory.CreateDirectory(PathFor("runs")).FullName;
}

/// <summary>Environment variables that change how the agent CLIs authenticate.</summary>
public static class HubwrightEnvironment
{
    /// <summary>When any is set, the claude CLI bills an API account rather than a subscription login.</summary>
    public static readonly string[] ClaudeApiAuthVariables =
        ["ANTHROPIC_API_KEY", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY"];
}
