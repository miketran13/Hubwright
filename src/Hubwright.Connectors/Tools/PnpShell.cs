using System.Diagnostics;
using System.Text.RegularExpressions;
using Hubwright.Implementation;
using Hubwright.Runtime;

namespace Hubwright.Tools;

/// <summary>Whether PowerShell 7 and PnP PowerShell are ready on this PC.</summary>
public sealed record PnpStatus(string? PowerShell, string? PowerShellVersion, string? PnpVersion, string Detail)
{
    public bool Ready => PowerShell is not null && PnpVersion is not null;
}

/// <summary>The output of one script run.</summary>
public sealed record PnpRun(int ExitCode, string Output, string Error)
{
    /// <summary>The script's own summary line, if it got that far.</summary>
    public string? Result => Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(PnpScripts.ResultMarker, StringComparison.Ordinal))?[PnpScripts.ResultMarker.Length..].Trim();

    /// <summary>The value a baseline script replaced (Set-PnPTenant), so it can be restored.</summary>
    public string? Previous => Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(PnpScripts.PreviousMarker, StringComparison.Ordinal))?[PnpScripts.PreviousMarker.Length..].Trim();

    /// <summary>The exception message the script reported when it stopped, if it did.</summary>
    public string? Failure => Error.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(PnpScripts.ErrorMarker, StringComparison.Ordinal))?[PnpScripts.ErrorMarker.Length..].Trim();
}

/// <summary>Runs PnP PowerShell scripts; tests replace it.</summary>
public interface IPnpShell
{
    Task<PnpRun> RunAsync(string script, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken);
}

/// <summary>
/// PowerShell 7 with PnP PowerShell, run as a child process. Scripts come from <see cref="PnpScripts"/>
/// only and may use only its cmdlets; the access token is passed in an environment variable, never on
/// the command line or in the script file, and the script file is deleted after the run.
/// </summary>
public sealed partial class PnpShell : IPnpShell
{
    /// <summary>PnP PowerShell needs PowerShell 7.4.0 or later (pnp.github.io/powershell/articles/installation.html).</summary>
    public static readonly Version MinimumPowerShell = new(7, 4, 0);

    public const string InstallPnpCommand = "Install-Module PnP.PowerShell -Scope CurrentUser";
    public const string InstallPowerShellCommand = "winget install --id Microsoft.PowerShell --source winget";

    /// <summary>
    /// Prints the PowerShell version, then the newest PnP PowerShell version. Both go through ToString():
    /// a bare Version object is written as a Major/Minor/Build/Revision table, not as "3.4.1".
    /// </summary>
    internal const string VersionScript = "$PSVersionTable.PSVersion.ToString(); $m = Get-Module -ListAvailable PnP.PowerShell | Sort-Object Version -Descending | Select-Object -First 1; if ($m) { $m.Version.ToString() }";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>pwsh on PATH, or in its default install folder on Windows.</summary>
    public static string? FindPowerShell()
    {
        var name = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), name)).FirstOrDefault(File.Exists);
        if (onPath is not null)
        {
            return onPath;
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Path.Combine(programFiles, "PowerShell", "7", name);
        return File.Exists(installed) ? installed : null;
    }

    public static async Task<PnpStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        var pwsh = FindPowerShell();
        if (pwsh is null)
        {
            return new PnpStatus(null, null, null, $"PowerShell 7 is not installed. Install it ({InstallPowerShellCommand}), then PnP PowerShell.");
        }

        var (_, output, _) = await RunProcessAsync(pwsh, ["-NoProfile", "-NonInteractive", "-Command", VersionScript], null, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var psVersion = lines.FirstOrDefault();
        var pnp = lines.Skip(1).FirstOrDefault(l => VersionLine().IsMatch(l));
        if (psVersion is not null && Version.TryParse(psVersion.Split('-')[0], out var v) && v < MinimumPowerShell)
        {
            return new PnpStatus(pwsh, psVersion, pnp, $"PowerShell {psVersion} is too old: PnP PowerShell needs {MinimumPowerShell} or later. Update it ({InstallPowerShellCommand}).");
        }

        return pnp is null
            ? new PnpStatus(pwsh, psVersion, null, $"PowerShell {psVersion} is ready; PnP PowerShell is not installed ({InstallPnpCommand}).")
            : new PnpStatus(pwsh, psVersion, pnp, $"PowerShell {psVersion} with PnP PowerShell {pnp}.");
    }

    public async Task<PnpRun> RunAsync(string script, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(environment);
        CheckCmdlets(script);
        CheckTenantParameters(script);
        var pwsh = FindPowerShell() ?? throw new InvalidOperationException($"PowerShell 7 is not installed: install it ({InstallPowerShellCommand}) and PnP PowerShell ({InstallPnpCommand}), from Setup → Prerequisites.");
        var folder = Directory.CreateDirectory(HubwrightHome.PathFor("tmp")).FullName;
        var path = Path.Combine(folder, $"pnp-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(path, script, cancellationToken).ConfigureAwait(false);
        try
        {
            var (exit, output, error) = await RunProcessAsync(pwsh, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path], environment, Timeout, cancellationToken).ConfigureAwait(false);
            return new PnpRun(exit, output, error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<(int Exit, string Output, string Error)> RunProcessAsync(string file, IEnumerable<string> arguments, IReadOnlyDictionary<string, string>? environment, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {file}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"PowerShell did not finish within {timeout.TotalMinutes:0} minutes.");
        }

        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    /// <summary>Only PnpScripts' cmdlets (and a few PowerShell helpers) may appear outside quoted strings.</summary>
    public static void CheckCmdlets(string script)
    {
        ArgumentNullException.ThrowIfNull(script);

        // Look at code only: titles and URLs inside single-quoted strings are data.
        foreach (Match match in CmdletPattern().Matches(QuotedString().Replace(script, "''")))
        {
            var cmdlet = match.Value;
            if (!PnpScripts.Cmdlets.Contains(cmdlet, StringComparer.OrdinalIgnoreCase) && cmdlet is not ("Add-Node" or "Import-Module" or "Write-Output" or "Where-Object" or "Select-Object" or "Join-Path" or "Invoke-WebRequest" or "Remove-Item" or "Out-Null"))
            {
                throw new InvalidOperationException($"Hubwright does not run {cmdlet}: it is not one of its PnP steps' cmdlets.");
            }
        }
    }

    /// <summary>Set-PnPTenant may only use the baseline's documented parameters (PnpScripts.TenantParameters).</summary>
    public static void CheckTenantParameters(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        foreach (var line in QuotedString().Replace(script, "''").Split('\n').Where(l => l.Contains("Set-PnPTenant", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (Match parameter in ParameterPattern().Matches(line))
            {
                if (!PnpScripts.TenantParameters.Contains(parameter.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Hubwright does not run Set-PnPTenant -{parameter.Groups[1].Value}: it is not one of its baseline parameters.");
                }
            }
        }
    }

    [GeneratedRegex(@"(?<![\w$])-([A-Za-z]+)\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ParameterPattern();

    [GeneratedRegex(@"\b[A-Z][a-z]+-[A-Za-z]+\b", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CmdletPattern();

    [GeneratedRegex(@"'(?:[^']|'')*'", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuotedString();

    [GeneratedRegex(@"^\d+\.\d+(\.\d+)*$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionLine();
}
