namespace Hubwright.Cli;

/// <summary>Minimal argument parsing: positional words, <c>--name value</c> options and <c>--flag</c> switches.</summary>
internal sealed class Args
{
    /// <summary>Options that never take a value, so the next word stays positional.</summary>
    private static readonly HashSet<string> Switches = new(StringComparer.OrdinalIgnoreCase) { "ai", "no-ai", "all", "open", "force", "help", "verbose", "yes", "all-automatic", "install", "no-prompt", "strict" };

    private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

    public Args(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg[2..];
                var eq = name.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0)
                {
                    _options[name[..eq]] = name[(eq + 1)..];
                }
                else if (!Switches.Contains(name) && i + 1 < list.Count && !list[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    _options[name] = list[++i];
                }
                else
                {
                    _options[name] = null;
                }
            }
            else
            {
                Positional.Add(arg);
            }
        }
    }

    public List<string> Positional { get; } = [];

    public string? At(int index) => index < Positional.Count ? Positional[index] : null;

    public string? Option(string name) => _options.TryGetValue(name, out var value) ? value : null;

    public bool Flag(string name) => _options.ContainsKey(name);

    public int Int(string name, int fallback) => int.TryParse(Option(name), out var value) ? value : fallback;
}
