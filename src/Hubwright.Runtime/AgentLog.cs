using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hubwright.Runtime;

/// <summary>One agent call in a run, as listed in <c>agent-sessions.jsonl</c>.</summary>
public sealed record AgentSessionEntry
{
    public required string Id { get; init; }
    public required string Agent { get; init; }
    public required string Backend { get; init; }
    public string? Model { get; init; }
    public string? Stage { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public bool? Succeeded { get; init; }
    public double? Seconds { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public decimal? CostUsd { get; init; }
    public bool CustomInstructions { get; init; }
    /// <summary>File name under the run's <c>transcripts/</c> folder.</summary>
    public string? Transcript { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Records lifecycle and usage metadata. Raw prompts, tool streams and responses are not logged.
/// Historical transcripts remain readable for explicit retention/migration.
/// A CLI process runs one run, so the run is process-wide state set when the run's services are built.
/// </summary>
public static class AgentLog
{
    public const string LogFileName = "agent-session.log";
    public const string IndexFileName = "agent-sessions.jsonl";
    public const string TranscriptsDirectory = "transcripts";

    internal static readonly object Gate = new();

    /// <summary>No byte-order mark: each line of the index must parse as JSON on its own.</summary>
    internal static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly AsyncLocal<(string Agent, string? Stage, string? Instructions)?> Role = new();

    /// <summary>The run the agent calls belong to; null outside a run (nothing is written then).</summary>
    public static string? RunDirectory { get; set; }

    /// <summary>Receives each live line; the CLI writes them to its console.</summary>
    public static Action<string>? Echo { get; set; }

    /// <summary>
    /// Receives each readable line of an agent's live output with the agent's name, for the run's
    /// step log (the playbook runner sets it). Lines can hold tool output: the observer keeps only
    /// the agent's own words, redacted.
    /// </summary>
    public static Action<string, string>? Observer { get; set; }

    public static Func<string, string> DecodeProtectedText { get; set; } = text => text;

    /// <summary>
    /// Names the agent and stage for calls made in this async flow (a role agent sets it), and the
    /// developer's instructions its system prompt carries, so the log shows them apart.
    /// </summary>
    public static IDisposable As(string agent, string? stage, string? instructions = null)
    {
        var previous = Role.Value;
        Role.Value = (agent, stage, instructions);
        return new Restore(() => Role.Value = previous);
    }

    public static AgentCall Begin(string backend, string? model, string prompt, string? systemPrompt = null,
        string? instructions = null, string? agent = null, string? stage = null, bool transcript = true)
    {
        var role = Role.Value;
        return new AgentCall(
            agent ?? role?.Agent ?? backend,
            backend,
            model,
            stage ?? role?.Stage,
            prompt,
            systemPrompt,
            instructions ?? role?.Instructions,
            null,
            RunDirectory);
    }

    /// <summary>The calls of a run, latest state of each, in start order.</summary>
    // The budget guard totals today's spend across every run folder before each model call; an
    // unchanged usage file (most of them: finished runs) is not parsed again.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Written, long Length, IReadOnlyList<AgentSessionEntry> Entries)> Parsed = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AgentSessionEntry> Read(string runDirectory, bool strict = false)
    {
        var path = Path.Combine(runDirectory, IndexFileName);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return [];
        }

        var key = (strict ? "s|" : "l|") + path;
        if (Parsed.TryGetValue(key, out var known) && known.Written == file.LastWriteTimeUtc && known.Length == file.Length)
        {
            return known.Entries;
        }

        var entries = ReadAll(path, strict);
        if (Parsed.Count > 5000)
        {
            Parsed.Clear();
        }

        Parsed[key] = (file.LastWriteTimeUtc, file.Length, entries);
        return entries;
    }

    private static IReadOnlyList<AgentSessionEntry> ReadAll(string path, bool strict)
    {
        var byId = new Dictionary<string, AgentSessionEntry>();
        var order = new List<string>();
        foreach (var line in SafeLines(path))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<AgentSessionEntry>(line, Json);
                if (entry is null && strict)
                {
                    throw new JsonException("The usage ledger contains a null entry.");
                }

                if (entry is not null)
                {
                    if (!byId.ContainsKey(entry.Id))
                    {
                        order.Add(entry.Id);
                    }

                    byId[entry.Id] = entry;
                }
            }
            catch (JsonException) when (!strict)
            {
                // A line being written while we read; the next read has it.
            }
        }

        return [.. order.Select(id => byId[id])];
    }

    /// <summary>The live log of a run from <paramref name="offset"/> on, and the offset to read from next.</summary>
    public static (string Text, long Next) ReadLog(string runDirectory, long offset = 0)
    {
        var path = Path.Combine(runDirectory, LogFileName);
        if (!File.Exists(path))
        {
            return ("", 0);
        }

        using (var probe = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
        {
            var prefix = new char[7];
            if (probe.Read(prefix, 0, prefix.Length) == prefix.Length && new string(prefix) == "TPENC1:")
            {
                var decoded = DecodeProtectedText(File.ReadAllText(path));
                var start = (int)Math.Clamp(offset, 0, decoded.Length);
                return (decoded[start..], decoded.Length);
            }
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (offset > stream.Length || offset < 0)
        {
            offset = 0;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Utf8);
        var text = reader.ReadToEnd();
        return (text, stream.Length);
    }

    private static IEnumerable<string> SafeLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Utf8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                yield return DecodeProtectedText(line);
            }
        }
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>One agent call: stream its output with <see cref="Line"/>, then <see cref="End"/> it.</summary>
public sealed class AgentCall
{
    private readonly string? _transcriptRun;
    private readonly string? _run;
    private readonly string _prompt;
    private readonly string? _systemPrompt;
    private readonly string? _instructions;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StringBuilder _live = new();
    private AgentSessionEntry _entry;
    private bool _ended;

    internal AgentCall(string agent, string backend, string? model, string? stage, string prompt, string? systemPrompt,
        string? instructions, string? transcriptRun, string? run)
    {
        _prompt = prompt;
        _systemPrompt = systemPrompt;
        _instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions;
        _transcriptRun = transcriptRun;
        _run = run;
        _entry = new AgentSessionEntry
        {
            Id = $"{DateTime.UtcNow:HHmmssfff}-{Guid.NewGuid():N}"[..20],
            Agent = agent,
            Backend = backend,
            Model = model,
            Stage = stage,
            StartedAt = DateTimeOffset.Now,
            CustomInstructions = _instructions is not null,
        };

        var header = $"▶ {agent} · {backend}{(model is null ? "" : $" ({model})")}{(stage is null ? "" : $" · {stage}")}"
            + (_instructions is null ? "" : " · with custom instructions");
        Write(header, log: $"\n=== {_entry.StartedAt:yyyy-MM-dd HH:mm:ss} {header[2..]}");
        Index();
    }

    public string Agent => _entry.Agent;

    /// <summary>One line of the agent's live output (already made readable by the caller).</summary>
    public void Line(string? text, bool words = false)
    {
        if (string.IsNullOrWhiteSpace(text) || _ended)
        {
            return;
        }

        // Raw CLI events can contain private tool results. Only lifecycle/usage metadata is logged;
        // the run's step log takes the agent's own words (its observer decides what to keep).
        if (words)
        {
            AgentLog.Observer?.Invoke(_entry.Agent, text);
        }
    }

    /// <summary>Ends the call: the summary line, the index entry and the transcript.</summary>
    public void End(bool succeeded, string output, string? error = null, long? inputTokens = null, long? outputTokens = null,
        decimal? costUsd = null, string? rawOutput = null)
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        var seconds = _clock.Elapsed.TotalSeconds;
        var transcript = WriteTranscript(succeeded, output, error, rawOutput);
        _entry = _entry with
        {
            EndedAt = DateTimeOffset.Now,
            Succeeded = succeeded,
            Seconds = Math.Round(seconds, 1),
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CostUsd = costUsd,
            Transcript = transcript,
            Error = error is null ? null : "Agent call failed; see the redacted run error.",
        };

        var usage = inputTokens is null && outputTokens is null ? "" : $" · {inputTokens:N0} in / {outputTokens:N0} out";
        var cost = costUsd is { } c ? $" · ${c:0.00}" : "";
        Write($"■ {_entry.Agent} {(succeeded ? "done" : "failed")} in {seconds:0}s{usage}{cost}");
        Index();
    }

    private void Write(string line, string? log = null)
    {
        AgentLog.Echo?.Invoke(line);
        if (_run is null)
        {
            return;
        }

        try
        {
            lock (AgentLog.Gate)
            {
                Directory.CreateDirectory(_run);
                File.AppendAllText(Path.Combine(_run, AgentLog.LogFileName), (log ?? line) + Environment.NewLine, AgentLog.Utf8);
            }
        }
        catch (IOException)
        {
            // Logging must never fail the run.
        }
    }

    private void Index()
    {
        if (_run is null)
        {
            return;
        }

        lock (AgentLog.Gate)
        {
            Directory.CreateDirectory(_run);
            File.AppendAllText(Path.Combine(_run, AgentLog.IndexFileName),
                JsonSerializer.Serialize(_entry, AgentLog.Json) + Environment.NewLine, AgentLog.Utf8);
        }
    }

    private string? WriteTranscript(bool succeeded, string output, string? error, string? rawOutput)
    {
        if (_transcriptRun is null)
        {
            return null;
        }

        try
        {
            var directory = Path.Combine(_transcriptRun, AgentLog.TranscriptsDirectory);
            Directory.CreateDirectory(directory);
            var safeAgent = string.Concat(_entry.Agent.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-'));
            var name = $"{(_entry.Stage ?? "agent").ToLowerInvariant()}-{_entry.StartedAt:HHmmss}-{safeAgent}.md";

            var text = new StringBuilder()
                .AppendLine($"# {_entry.Agent} — {_entry.Stage ?? "agent call"}")
                .AppendLine()
                .AppendLine($"- backend: {_entry.Backend}")
                .AppendLine($"- model: {_entry.Model ?? "(the CLI's default)"}")
                .AppendLine($"- started: {_entry.StartedAt:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"- result: {(succeeded ? "succeeded" : "failed")}{(error is null ? "" : $" — {error}")}")
                .AppendLine();

            if (_systemPrompt is not null)
            {
                Fence(text, "System prompt", _systemPrompt);
            }

            if (_instructions is not null)
            {
                Fence(text, "Custom instructions", _instructions);
            }

            Fence(text, "Prompt", _prompt);
            if (_live.Length > 0)
            {
                Fence(text, "Live output", _live.ToString());
            }

            Fence(text, "Answer", output);
            if (rawOutput is not null && rawOutput != output)
            {
                Fence(text, "Raw output", rawOutput);
            }

            File.WriteAllText(Path.Combine(directory, name), text.ToString(), AgentLog.Utf8);
            return name;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void Fence(StringBuilder text, string title, string body)
        => text.AppendLine($"## {title}").AppendLine().AppendLine("`````").AppendLine(body.TrimEnd()).AppendLine("`````").AppendLine();

    private static string Short(string value)
    {
        var line = value.Replace('\n', ' ').Replace('\r', ' ');
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}
