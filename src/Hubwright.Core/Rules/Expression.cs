using System.Collections;
using System.Globalization;
using System.Text;

namespace Hubwright.Rules;

/// <summary>
/// The rule language: a small, side-effect-free expression over named facts. Skills are data that
/// anyone can contribute, so rules are parsed into this tree and evaluated; nothing is compiled or
/// executed as code.
/// <para>
/// Grammar: <c>or</c>/<c>and</c>/<c>not</c>, comparisons (<c>== != &gt; &gt;= &lt; &lt;=</c>,
/// <c>in [..]</c>), <c>+ - * /</c>, literals (numbers, <c>'text'</c>, <c>true</c>, <c>false</c>,
/// <c>null</c>), fact names (<c>settings.sharingLevel</c>), parameters (<c>$maxNavLinks</c>) and
/// functions (<c>count</c>, <c>days_since</c>, <c>lower</c>, <c>contains</c>, <c>coalesce</c>,
/// <c>known</c>).
/// </para>
/// <para>
/// Logic is three-valued. A fact that was not collected is <see cref="Unknown"/>, and unknown flows
/// through comparisons, so a rule over missing data is "not assessed", never a silent pass.
/// </para>
/// </summary>
public abstract class Expression
{
    /// <summary>The value of anything that depends on a fact that was not collected.</summary>
    public static readonly object Unknown = new UnknownValue();

    public abstract object? Evaluate(IFactSource facts);

    /// <summary>Every fact name the expression reads, for validation against the fact catalog.</summary>
    public abstract IEnumerable<string> Facts();

    public static Expression Parse(string text) => new Parser(text).ParseAll();

    /// <summary>Evaluates to true, false or null (unknown).</summary>
    public bool? Test(IFactSource facts) => Evaluate(facts) switch
    {
        bool b => b,
        UnknownValue => null,
        null => null,
        var other => throw new RuleException($"The condition must be true or false, but it is {Describe(other)}."),
    };

    internal static string Describe(object? value) => value switch
    {
        null => "null",
        UnknownValue => "unknown",
        string s => $"'{s}'",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private sealed class UnknownValue
    {
        public override string ToString() => "unknown";
    }

    internal static bool IsUnknown(object? value) => value is UnknownValue;
}

/// <summary>Resolves fact names (and <c>$parameters</c>) to values for one rule target.</summary>
public interface IFactSource
{
    /// <summary>The fact's value; <see cref="Expression.Unknown"/> when it was not collected; throws for a name that does not exist.</summary>
    object? Get(string name);

    object? Parameter(string name);
}

/// <summary>A rule that cannot be parsed or evaluated; a skill author's mistake, reported with the rule id.</summary>
public sealed class RuleException(string message) : Exception(message);

internal sealed class Literal(object? value) : Expression
{
    public object? Value => value;

    public override object? Evaluate(IFactSource facts) => value;

    public override IEnumerable<string> Facts() => [];
}

internal sealed class FactRef(string name) : Expression
{
    public string Name => name;

    public override object? Evaluate(IFactSource facts) => Values.Normalize(facts.Get(name));

    public override IEnumerable<string> Facts() => [name];
}

internal sealed class ParamRef(string name) : Expression
{
    public override object? Evaluate(IFactSource facts) => Values.Normalize(facts.Parameter(name));

    public override IEnumerable<string> Facts() => [];
}

internal sealed class ListExpr(IReadOnlyList<Expression> items) : Expression
{
    public override object? Evaluate(IFactSource facts) => items.Select(i => i.Evaluate(facts)).ToList();

    public override IEnumerable<string> Facts() => items.SelectMany(i => i.Facts());
}

internal sealed class Unary(string op, Expression operand) : Expression
{
    public override object? Evaluate(IFactSource facts)
    {
        var value = operand.Evaluate(facts);
        if (IsUnknown(value) || value is null)
        {
            return Unknown;
        }

        return op switch
        {
            "not" => value is bool b ? !b : throw new RuleException($"'not' needs true or false, not {Describe(value)}."),
            "-" => -Values.Number(value),
            _ => throw new RuleException($"Unknown operator {op}."),
        };
    }

    public override IEnumerable<string> Facts() => operand.Facts();
}

internal sealed class Binary(string op, Expression left, Expression right) : Expression
{
    public override object? Evaluate(IFactSource facts)
    {
        if (op is "and" or "or")
        {
            return Logic(facts);
        }

        var a = left.Evaluate(facts);
        var b = right.Evaluate(facts);

        // Equality with an explicit null literal asks "is this value missing?"; it is never unknown.
        if (op is "==" or "!=" && (right is Literal { Value: null } || left is Literal { Value: null }))
        {
            var missing = (left is Literal { Value: null } ? b : a) is null || IsUnknown(left is Literal { Value: null } ? b : a);
            return op == "==" ? missing : !missing;
        }

        if (IsUnknown(a) || IsUnknown(b) || a is null || b is null)
        {
            return Unknown;
        }

        return op switch
        {
            "==" => Values.AreEqual(a, b),
            "!=" => !Values.AreEqual(a, b),
            ">" => Values.Compare(a, b) > 0,
            ">=" => Values.Compare(a, b) >= 0,
            "<" => Values.Compare(a, b) < 0,
            "<=" => Values.Compare(a, b) <= 0,
            "in" => b is IEnumerable list and not string
                ? list.Cast<object?>().Any(item => item is not null && Values.AreEqual(a, item))
                : throw new RuleException("'in' needs a list on the right, for example x in ['a', 'b']."),
            "+" => Values.Number(a) + Values.Number(b),
            "-" => Values.Number(a) - Values.Number(b),
            "*" => Values.Number(a) * Values.Number(b),
            "/" => Values.Number(b) == 0 ? Unknown : Values.Number(a) / Values.Number(b),
            _ => throw new RuleException($"Unknown operator {op}."),
        };
    }

    // Kleene logic: false and unknown = false; true or unknown = true.
    private object Logic(IFactSource facts)
    {
        var a = AsLogic(left.Evaluate(facts));
        if (op == "and" && a == false)
        {
            return false;
        }

        if (op == "or" && a == true)
        {
            return true;
        }

        var b = AsLogic(right.Evaluate(facts));
        return op == "and"
            ? a == true && b == true ? true : b == false ? false : Unknown
            : a == true || b == true ? true : a == false && b == false ? false : Unknown;
    }

    private static bool? AsLogic(object? value) => value switch
    {
        bool b => b,
        null => null,
        _ when IsUnknown(value) => null,
        _ => throw new RuleException($"'and'/'or' need true or false, not {Describe(value)}."),
    };

    public override IEnumerable<string> Facts() => left.Facts().Concat(right.Facts());
}

internal sealed class Call(string name, IReadOnlyList<Expression> args) : Expression
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "count", "days_since", "lower", "contains", "coalesce", "known",
    };

    public override object? Evaluate(IFactSource facts)
    {
        var values = args.Select(a => a.Evaluate(facts)).ToList();
        switch (name.ToLowerInvariant())
        {
            case "known":
                Arity(1);
                return values[0] is not null && !IsUnknown(values[0]);
            case "coalesce":
                return values.FirstOrDefault(v => v is not null && !IsUnknown(v)) ?? Unknown;
        }

        if (values.Any(v => v is null || IsUnknown(v)))
        {
            return Unknown;
        }

        switch (name.ToLowerInvariant())
        {
            case "count":
                Arity(1);
                return values[0] is ICollection c ? c.Count
                    : values[0] is IEnumerable e and not string ? e.Cast<object?>().Count()
                    : throw new RuleException("count() needs a list.");
            case "days_since":
                Arity(1);
                return values[0] is DateTimeOffset d
                    ? Math.Floor((Values.Now - d).TotalDays)
                    : throw new RuleException("days_since() needs a date.");
            case "lower":
                Arity(1);
                return Values.Text(values[0]!).ToLowerInvariant();
            case "contains":
                Arity(2);
                return Values.Text(values[0]!).Contains(Values.Text(values[1]!), StringComparison.OrdinalIgnoreCase);
            default:
                throw new RuleException($"Unknown function {name}(). Known: {string.Join(", ", Names)}.");
        }
    }

    private void Arity(int expected)
    {
        if (args.Count != expected)
        {
            throw new RuleException($"{name}() takes {expected} argument(s), not {args.Count}.");
        }
    }

    public override IEnumerable<string> Facts() => args.SelectMany(a => a.Facts());
}

/// <summary>How the rule language compares and converts values.</summary>
public static class Values
{
    /// <summary>The clock rules read (days_since). Tests pin it.</summary>
    public static DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

    internal static object? Normalize(object? value) => value switch
    {
        null => null,
        int i => (double)i,
        long l => (double)l,
        decimal m => (double)m,
        float f => (double)f,
        Enum e => e.ToString(),
        _ => value,
    };

    internal static double Number(object value) => value switch
    {
        double d => d,
        int i => i,
        long l => l,
        bool => throw new RuleException("Expected a number but got true/false."),
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => throw new RuleException($"Expected a number but got {Expression.Describe(value)}."),
    };

    internal static string Text(object value) => Convert.ToString(Normalize(value), CultureInfo.InvariantCulture) ?? "";

    internal static bool AreEqual(object a, object b)
    {
        a = Normalize(a)!;
        b = Normalize(b)!;
        return (a, b) switch
        {
            (double x, double y) => Math.Abs(x - y) < 1e-9,
            (bool x, bool y) => x == y,
            (string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase),
            _ => Equals(a, b),
        };
    }

    internal static int Compare(object a, object b)
    {
        a = Normalize(a)!;
        b = Normalize(b)!;
        return (a, b) switch
        {
            (DateTimeOffset x, DateTimeOffset y) => x.CompareTo(y),
            (string x, string y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
            _ => Number(a).CompareTo(Number(b)),
        };
    }
}

internal sealed class Parser(string text)
{
    private readonly List<Token> _tokens = Tokenize(text);
    private int _at;

    private sealed record Token(string Kind, string Text, int Position);

    public Expression ParseAll()
    {
        var expression = Or();
        if (Peek().Kind != "end")
        {
            throw Error($"Unexpected '{Peek().Text}'");
        }

        return expression;
    }

    private Token Peek() => _tokens[_at];

    private Token Next() => _tokens[_at++];

    private bool Accept(string kind, string? value = null)
    {
        var token = Peek();
        if (token.Kind == kind && (value is null || string.Equals(token.Text, value, StringComparison.OrdinalIgnoreCase)))
        {
            _at++;
            return true;
        }

        return false;
    }

    private void Expect(string kind, string value)
    {
        if (!Accept(kind, value))
        {
            throw Error($"Expected '{value}' but found '{Peek().Text}'");
        }
    }

    private RuleException Error(string message) => new($"{message} at position {Peek().Position + 1} in: {text}");

    private Expression Or()
    {
        var left = And();
        while (Accept("word", "or"))
        {
            left = new Binary("or", left, And());
        }

        return left;
    }

    private Expression And()
    {
        var left = Not();
        while (Accept("word", "and"))
        {
            left = new Binary("and", left, Not());
        }

        return left;
    }

    private Expression Not() => Accept("word", "not") ? new Unary("not", Not()) : Comparison();

    private Expression Comparison()
    {
        var left = Sum();
        var token = Peek();
        if (token.Kind == "op" && token.Text is "==" or "!=" or ">" or ">=" or "<" or "<=")
        {
            Next();
            return new Binary(token.Text, left, Sum());
        }

        if (Accept("word", "in"))
        {
            return new Binary("in", left, Sum());
        }

        return left;
    }

    private Expression Sum()
    {
        var left = Product();
        while (Peek() is { Kind: "op", Text: "+" or "-" } token)
        {
            Next();
            left = new Binary(token.Text, left, Product());
        }

        return left;
    }

    private Expression Product()
    {
        var left = UnaryMinus();
        while (Peek() is { Kind: "op", Text: "*" or "/" } token)
        {
            Next();
            left = new Binary(token.Text, left, UnaryMinus());
        }

        return left;
    }

    private Expression UnaryMinus()
    {
        if (Peek() is { Kind: "op", Text: "-" })
        {
            Next();
            return new Unary("-", UnaryMinus());
        }

        return Primary();
    }

    private Expression Primary()
    {
        var token = Next();
        switch (token.Kind)
        {
            case "number":
                return new Literal(double.Parse(token.Text, CultureInfo.InvariantCulture));
            case "string":
                return new Literal(token.Text);
            case "param":
                return new ParamRef(token.Text);
            case "(":
                var inner = Or();
                Expect(")", ")");
                return inner;
            case "[":
                var items = new List<Expression>();
                if (!Accept("]", "]"))
                {
                    do
                    {
                        items.Add(Or());
                    }
                    while (Accept(",", ","));
                    Expect("]", "]");
                }

                return new ListExpr(items);
            case "word":
                switch (token.Text.ToLowerInvariant())
                {
                    case "true":
                        return new Literal(true);
                    case "false":
                        return new Literal(false);
                    case "null":
                        return new Literal(null);
                }

                if (Accept("(", "("))
                {
                    if (!Call.Names.Contains(token.Text))
                    {
                        throw new RuleException($"Unknown function {token.Text}(). Known: {string.Join(", ", Call.Names)}.");
                    }

                    var args = new List<Expression>();
                    if (!Accept(")", ")"))
                    {
                        do
                        {
                            args.Add(Or());
                        }
                        while (Accept(",", ","));
                        Expect(")", ")");
                    }

                    return new Call(token.Text, args);
                }

                return new FactRef(token.Text);
            default:
                _at--;
                throw Error(token.Kind == "end" ? "The expression ends too early" : $"Unexpected '{token.Text}'");
        }
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var start = i;
            if (char.IsDigit(c))
            {
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new("number", text[start..i].Replace("_", "", StringComparison.Ordinal), start));
            }
            else if (c is '\'' or '"')
            {
                var quote = c;
                var value = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    value.Append(text[i++]);
                }

                if (i >= text.Length)
                {
                    throw new RuleException($"Unclosed text starting at position {start + 1} in: {text}");
                }

                i++;
                tokens.Add(new("string", value.ToString(), start));
            }
            else if (char.IsLetter(c) || c is '_' or '$')
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.'))
                {
                    i++;
                }

                var word = text[start..i];
                tokens.Add(word.StartsWith('$') ? new("param", word[1..], start) : new("word", word, start));
            }
            else if (i + 1 < text.Length && text.Substring(i, 2) is "==" or "!=" or ">=" or "<=")
            {
                tokens.Add(new("op", text.Substring(i, 2), start));
                i += 2;
            }
            else if (c is '>' or '<' or '+' or '-' or '*' or '/')
            {
                tokens.Add(new("op", c.ToString(), start));
                i++;
            }
            else if (c is '(' or ')' or '[' or ']' or ',')
            {
                tokens.Add(new(c.ToString(), c.ToString(), start));
                i++;
            }
            else
            {
                throw new RuleException($"Unexpected character '{c}' at position {i + 1} in: {text}");
            }
        }

        tokens.Add(new("end", "end of expression", text.Length));
        return tokens;
    }
}
