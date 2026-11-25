using System.Globalization;

namespace Vespa.Models.Schema;

/// <summary>A Vespa ranking expression composed from rank features and arithmetic.</summary>
public sealed class RankExpression
{
    private readonly string _text;
    private readonly bool _isRaw;

    private RankExpression(string text, bool isRaw = false) => (_text, _isRaw) = (text, isRaw);

    /// <summary>Uses a native Vespa ranking expression. Vespa validates it during deployment.</summary>
    public static RankExpression Raw(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return new RankExpression(expression, isRaw: true);
    }

    /// <summary>References the value of an attribute field.</summary>
    public static RankExpression Attribute(string field) =>
        new($"attribute({ValidatePath(field, nameof(field))})");

    /// <summary>Scores the given indexed string field, which must enable BM25.</summary>
    public static RankExpression Bm25(string field) => new($"bm25({ValidatePath(field, nameof(field))})");

    /// <summary>Scores vector proximity for a nearest-neighbor query on the given field.</summary>
    public static RankExpression Closeness(string field) =>
        new($"closeness(field, {ValidatePath(field, nameof(field))})");

    /// <summary>References a query input.</summary>
    public static RankExpression Query(string name) => new($"query({ValidatePath(name, nameof(name))})");

    /// <summary>References a function parameter or a rank feature without arguments.</summary>
    public static RankExpression Reference(string name) => new(ValidateIdentifier(name, nameof(name)));

    /// <summary>Calls a ranking function with the given arguments.</summary>
    public static RankExpression Call(string name, params RankExpression[] arguments)
    {
        ValidateIdentifier(name, nameof(name));
        ArgumentNullException.ThrowIfNull(arguments);
        if (Array.IndexOf(arguments, null) >= 0)
            throw new ArgumentException("Function arguments cannot be null.", nameof(arguments));

        return new RankExpression($"{name}({string.Join(", ", arguments.Select(argument => argument.AsOperand()))})");
    }

    /// <summary>Converts a finite number to a ranking literal.</summary>
    public static implicit operator RankExpression(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "A ranking literal must be finite.");
        return new RankExpression(value.ToString("R", CultureInfo.InvariantCulture));
    }

    /// <summary>Adds two ranking expressions.</summary>
    public static RankExpression operator +(RankExpression left, RankExpression right) => Binary(left, "+", right);

    /// <summary>Subtracts two ranking expressions.</summary>
    public static RankExpression operator -(RankExpression left, RankExpression right) => Binary(left, "-", right);

    /// <summary>Multiplies two ranking expressions.</summary>
    public static RankExpression operator *(RankExpression left, RankExpression right) => Binary(left, "*", right);

    /// <summary>Divides two ranking expressions.</summary>
    public static RankExpression operator /(RankExpression left, RankExpression right) => Binary(left, "/", right);

    /// <summary>Returns the native Vespa ranking expression.</summary>
    public override string ToString() => _text;

    private static RankExpression Binary(RankExpression left, string op, RankExpression right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new RankExpression($"({left.AsOperand()} {op} {right.AsOperand()})");
    }

    private string AsOperand() => _isRaw ? $"({_text})" : _text;

    internal static string ValidateIdentifier(string name, string paramName)
    {
        if (!IsIdentifier(name))
            throw new ArgumentException($"'{name}' is not a valid ranking identifier.", paramName);
        return name;
    }

    private static string ValidatePath(string name, string paramName)
    {
        if (name is null || !name.Split('.').All(IsIdentifier))
            throw new ArgumentException($"'{name}' is not a valid ranking field or input name.", paramName);
        return name;
    }

    private static bool IsIdentifier(string? name) =>
        !string.IsNullOrEmpty(name) && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
