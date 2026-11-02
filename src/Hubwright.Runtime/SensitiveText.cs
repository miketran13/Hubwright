using System.Text.RegularExpressions;

namespace Hubwright.Runtime;

/// <summary>Defense in depth for accidental credentials; not a substitute for scoped data access.</summary>
public static partial class SensitiveText
{
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = Credential().Replace(text, "[REDACTED CREDENTIAL]");
        foreach (var name in new[] { "ANTHROPIC_API_KEY", "GITHUB_TOKEN", "HUBWRIGHT_GITHUB_TOKEN", "AZURE_OPENAI_API_KEY" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: >= 8 } secret)
            {
                result = result.Replace(secret, "[REDACTED CREDENTIAL]", StringComparison.Ordinal);
            }
        }

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && name.StartsWith("HUBWRIGHT_SECRET_", StringComparison.OrdinalIgnoreCase)
                && entry.Value is string { Length: >= 8 } secret)
            {
                result = result.Replace(secret, "[REDACTED CREDENTIAL]", StringComparison.Ordinal);
            }
        }

        return result;
    }

    [GeneratedRegex(@"(?:sk-ant-[A-Za-z0-9_-]{12,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|(?i:bearer)\s+[A-Za-z0-9._~-]{12,})", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Credential();
}
