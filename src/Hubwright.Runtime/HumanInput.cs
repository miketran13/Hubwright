using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hubwright.Runtime;

/// <summary>
/// Something a running run needs from a person before it can go on: tool permissions for the
/// coding agent, a plan to review, or questions about the spec.
/// </summary>
public sealed record InputRequest
{
    public required string Id { get; init; }
    public required string RunId { get; init; }

    /// <summary>permissions | plan | clarify.</summary>
    public required string Kind { get; init; }

    public required string Title { get; init; }
    public string? Stage { get; init; }
    public string? Detail { get; init; }

    /// <summary>What the person is asked about; its shape depends on <see cref="Kind"/>.</summary>
    public JsonNode? Payload { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>The process waiting for the answer: a request whose process has gone is no longer waiting.</summary>
    public int ProcessId { get; init; } = Environment.ProcessId;
}

/// <summary>A person's answer to an <see cref="InputRequest"/>.</summary>
public sealed record InputAnswer
{
    /// <summary>approve | revise | deny.</summary>
    public string Decision { get; init; } = "approve";

    /// <summary>Free text: plan adjustments, or a note.</summary>
    public string? Text { get; init; }

    /// <summary>Kind-specific values: the permission rules allowed, the clarification answers.</summary>
    public JsonNode? Values { get; init; }

    /// <summary>For permissions: also keep them in the repo's settings for later runs.</summary>
    public bool Remember { get; init; }

    public string? By { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    public bool Approved => string.Equals(Decision, "approve", StringComparison.OrdinalIgnoreCase);
    public bool Revise => string.Equals(Decision, "revise", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A request with its answer, if it has one.</summary>
public sealed record InputExchange(InputRequest Request, InputAnswer? Answer);

/// <summary>
/// The run's line to a person, through files in the run directory: <c>inputs/&lt;id&gt;.request.json</c>
/// is written when the run needs something, and the run waits until <c>inputs/&lt;id&gt;.answer.json</c>
/// appears. The dashboard, the desktop app and <c>hubwright input</c> all answer the same way, so a run
/// started from any of them can be answered from any of them, while it keeps running.
/// </summary>
public sealed class HumanInput(string runDirectory, Action<string>? report = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string RunDirectory => runDirectory;

    /// <summary>How often the waiting run looks for an answer.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Also ask at the console (an interactive terminal); the first answer, there or in a file, wins.</summary>
    public bool Console { get; init; }

    /// <summary>Asks, then waits for the answer (or cancellation).</summary>
    public async Task<InputAnswer> AskAsync(InputRequest request, CancellationToken cancellationToken = default)
    {
        var directory = Directory.CreateDirectory(Path.Combine(runDirectory, "inputs")).FullName;
        var requestPath = Path.Combine(directory, $"{request.Id}.request.json");
        var answerPath = Path.Combine(directory, $"{request.Id}.answer.json");

        if (!File.Exists(requestPath))
        {
            WriteAtomic(requestPath, JsonSerializer.Serialize(request, Json));
        }

        report?.Invoke($"  [??] waiting for you: {request.Title} — answer it in Hubwright, or `hubwright input {request.RunId}`");

        if (Console)
        {
            // The console answer is written as the answer file, so the run's page shows who decided.
            _ = Task.Run(() =>
            {
                var typed = AskAtConsole(request);
                if (typed is not null && !File.Exists(answerPath))
                {
                    WriteAtomic(answerPath, JsonSerializer.Serialize(typed, Json));
                }
            }, CancellationToken.None);
        }

        while (true)
        {
            if (Read<InputAnswer>(answerPath) is { } answer)
            {
                report?.Invoke(Decided(request, answer));
                return answer;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The log line for a decision: "[ok] Approved by mike · Review the plan", with any note.</summary>
    public static string Decided(InputRequest request, InputAnswer answer)
    {
        var (mark, verb) = answer.Approved ? ("ok", "Approved") : answer.Revise ? ("..", "Changes requested") : ("no", "Rejected");
        var note = string.IsNullOrWhiteSpace(answer.Text) ? "" : $": {answer.Text.Trim().ReplaceLineEndings(" ")}";
        return $"  [{mark}] {verb} by {answer.By ?? "you"} · {request.Title}{note}";
    }

    /// <summary>The question at the console: yes, no, or free text (adjustments, rules, guidance).</summary>
    private static InputAnswer? AskAtConsole(InputRequest request)
    {
        var console = System.Console.Out;
        console.WriteLine();
        console.WriteLine($"  {request.Title}");
        if (request.Detail is { Length: > 0 } detail)
        {
            console.WriteLine($"  {detail}");
        }

        switch (request.Payload)
        {
            case JsonValue value when value.TryGetValue<string>(out var text):
                console.WriteLine();
                console.WriteLine(text);
                break;
            case JsonArray items:
                foreach (var item in items)
                {
                    console.WriteLine($"    - {item?["rule"] ?? item?["question"] ?? item}");
                }

                break;
        }

        var hint = request.Kind switch
        {
            "plan" => "[y] approve · [n] stop · or type what to change",
            "permissions" => "[y] allow all · [n] deny · or type the rules to allow, comma-separated",
            "tests" => "[y] more attempts · [n] stop · or type guidance for the agent",
            _ => "[y] yes · [n] no · or type an answer",
        };
        console.Write($"  {hint}\n  > ");
        var line = System.Console.ReadLine()?.Trim();
        if (line is null)
        {
            return null;
        }

        var by = Environment.UserName;
        if (line.Length == 0 || line.Equals("y", StringComparison.OrdinalIgnoreCase) || line.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return new InputAnswer { Decision = "approve", By = by };
        }

        if (line.Equals("n", StringComparison.OrdinalIgnoreCase) || line.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return new InputAnswer { Decision = "deny", By = by };
        }

        return request.Kind == "permissions"
            ? new InputAnswer { Decision = "approve", Values = new JsonArray([.. line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(r => (JsonNode)JsonValue.Create(r)!)]), By = by }
            : new InputAnswer { Decision = request.Kind == "tests" ? "approve" : "revise", Text = line, By = by };
    }

    /// <summary>Every request of a run, oldest first, with its answer if there is one.</summary>
    public static IReadOnlyList<InputExchange> All(string runDirectory)
    {
        var directory = Path.Combine(runDirectory, "inputs");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(directory, "*.request.json")
            .Select(path => Read<InputRequest>(path))
            .OfType<InputRequest>()
            .OrderBy(r => r.CreatedAt)
            .Select(r => new InputExchange(r, Read<InputAnswer>(Path.Combine(directory, $"{r.Id}.answer.json"))))];
    }

    /// <summary>The requests still waiting for a person (their run is still running).</summary>
    public static IReadOnlyList<InputRequest> Pending(string runDirectory)
        => [.. All(runDirectory).Where(e => e.Answer is null && IsAlive(e.Request.ProcessId)).Select(e => e.Request)];

    private static bool IsAlive(int processId)
    {
        if (processId <= 0)
        {
            return true;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Answers a waiting request. Throws when there is no such request or it was already answered.</summary>
    public static InputRequest Answer(string runDirectory, string requestId, InputAnswer answer)
    {
        if (requestId.IndexOfAny(['/', '\\']) >= 0 || requestId.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{requestId}' is not a request id.");
        }

        var directory = Path.Combine(runDirectory, "inputs");
        var request = Read<InputRequest>(Path.Combine(directory, $"{requestId}.request.json"))
            ?? throw new InvalidOperationException($"The run is not waiting for '{requestId}'.");
        var answerPath = Path.Combine(directory, $"{requestId}.answer.json");
        if (File.Exists(answerPath))
        {
            throw new InvalidOperationException("That was already answered.");
        }

        WriteAtomic(answerPath, JsonSerializer.Serialize(answer, Json));
        return request;
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null; // Half-written by the other side: the next poll reads it whole.
        }
    }

    /// <summary>Write then rename, so the waiting side never reads half a file.</summary>
    private static void WriteAtomic(string path, string text)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }
}
