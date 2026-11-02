using System.Diagnostics;
using System.Text;

namespace Hubwright.Runtime;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;
    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}\n{StdErr}";
}

/// <summary>
/// Every external tool (claude, copilot, git, gh, dotnet, npx, az) goes through here.
/// Centralizing it keeps Windows PATH/PATHEXT resolution, timeouts and deadlock-free stream
/// draining in one place rather than scattered across providers.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan? timeout = null,
        string? standardInput = null,
        Action<string>? onStdOutLine = null,
        IDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(fileName)
            ?? throw new FileNotFoundException(
                $"'{fileName}' was not found on PATH. Run `hubwright doctor` to see what is missing.", fileName);

        var psi = new ProcessStartInfo
        {
            FileName = resolved,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var started = Stopwatch.StartNew();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.Start();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } limit)
        {
            timeoutCts.CancelAfter(limit);
        }

        try
        {
            var output = DrainAsync(process.StandardOutput, stdout, onStdOutLine, process, timeoutCts.Token);
            var errors = DrainAsync(process.StandardError, stderr, null, process, timeoutCts.Token);
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutCts.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeoutCts.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new ProcessResult(
                124,
                stdout.ToString(),
                $"{stderr}\nProcess '{fileName}' exceeded its {timeout!.Value.TotalMinutes:0.#} minute budget and was terminated.",
                started.Elapsed);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        catch
        {
            TryKill(process);
            throw;
        }

        // WaitForExitAsync can return before the async readers have flushed; this drains them.
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString(), started.Elapsed);
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder target, Action<string>? onLine, Process process, CancellationToken cancellationToken)
    {
        const int maxCharacters = 2_000_000;
        var buffer = new char[4096];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (target.Length + count > maxCharacters)
            {
                TryKill(process);
                throw new InvalidDataException("Process output exceeded the 2,000,000-character per-stream limit and was terminated.");
            }

            target.Append(buffer, 0, count);
            if (onLine is not null)
            {
                for (var i = 0; i < count; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        onLine(line.ToString().TrimEnd('\r'));
                        line.Clear();
                    }
                    else
                    {
                        line.Append(buffer[i]);
                    }
                }
            }
        }

        if (line.Length > 0)
        {
            onLine?.Invoke(line.ToString());
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already gone.
        }
    }

    /// <summary>Returns true when the executable can be found on PATH.</summary>
    public static bool Exists(string fileName) => Resolve(fileName) is not null;

    /// <summary>
    /// Resolves a bare command name the way a shell would. Process.Start does not apply PATHEXT,
    /// so `git`/`npx` (which are .cmd shims on Windows) fail without this.
    /// </summary>
    public static string? Resolve(string fileName)
    {
        if (Path.IsPathRooted(fileName))
        {
            return File.Exists(fileName) ? fileName : null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathDirs)
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim('"'), fileName);
            }
            catch (ArgumentException)
            {
                continue; // Malformed PATH entry.
            }

            if (File.Exists(candidate) && (!OperatingSystem.IsWindows() || Path.HasExtension(candidate)))
            {
                return candidate;
            }

            foreach (var ext in extensions)
            {
                var withExt = candidate + ext;
                if (File.Exists(withExt))
                {
                    return withExt;
                }
            }
        }

        return null;
    }
}
