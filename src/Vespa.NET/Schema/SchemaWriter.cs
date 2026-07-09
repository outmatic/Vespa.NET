using System.Text;

namespace Vespa.Models.Schema;

/// <summary>
/// Minimal indent-aware writer for <c>.sd</c> schema text: <see cref="Open"/> writes
/// <c>header {</c> and indents, <see cref="Line"/> writes an indented line,
/// <see cref="Close"/> dedents and writes <c>}</c>. Replaces hand-counted spaces.
/// </summary>
internal sealed class SchemaWriter
{
    private const int IndentSize = 4;

    private readonly StringBuilder _sb = new();
    private int _depth;

    /// <summary>Writes <c>header {</c> and increases the indent level.</summary>
    public SchemaWriter Open(string header)
    {
        Line($"{header} {{");
        _depth++;
        return this;
    }

    /// <summary>Writes a single indented line.</summary>
    public SchemaWriter Line(string text)
    {
        _sb.Append(' ', _depth * IndentSize).Append(text).Append('\n');
        return this;
    }

    /// <summary>Decreases the indent level and writes <c>}</c>.</summary>
    public SchemaWriter Close()
    {
        if (_depth == 0)
            throw new InvalidOperationException("SchemaWriter.Close() called without a matching Open().");
        _depth--;
        return Line("}");
    }

    /// <inheritdoc />
    public override string ToString()
    {
        if (_depth != 0)
            throw new InvalidOperationException($"SchemaWriter has {_depth} unclosed block(s).");
        return _sb.ToString();
    }
}
