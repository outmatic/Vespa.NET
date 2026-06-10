using System.Linq.Expressions;
using System.Text;
using Vespa.Models.Attributes;

namespace Vespa.Query;

/// <summary>
/// Fluent builder for YQL query strings
/// </summary>
public sealed class YqlBuilder
{
    private string _select = "*";
    private string? _from;
    private YqlWhereClause? _where;
    private readonly List<(string Field, bool Descending)> _orderByClauses = [];
    private int? _limit;
    private int? _offset;
    private GroupingBuilder? _grouping;

    private YqlBuilder() { }

    internal static YqlBuilder Create(string select, string? from = null) =>
        new() { _select = select, _from = from };

    public static YqlBuilder Select(string fields = "*")
    {
        var builder = new YqlBuilder { _select = fields };
        return builder;
    }

    public YqlBuilder From(string documentType)
    {
        _from = documentType;
        return this;
    }

    /// <summary>
    /// Set the document type by reading <c>[VespaDocument]</c> on <typeparamref name="T"/>
    /// and returns a typed builder so that <c>Where</c> lambdas can omit the type argument
    /// when resolving field names via expressions.
    /// </summary>
    public YqlBuilder<T> From<T>() where T : class
    {
        _from = VespaDocumentMeta.For<T>().DocumentType;
        return new YqlBuilder<T>(this);
    }

    public YqlBuilder Where(Action<YqlWhereClause> configure)
    {
        _where = new YqlWhereClause();
        configure(_where);
        return this;
    }

    public YqlBuilder OrderBy(string field, bool descending = false)
    {
        _orderByClauses.Add((field, descending));
        return this;
    }

    public YqlBuilder Limit(int limit)
    {
        _limit = limit;
        return this;
    }

    public YqlBuilder Offset(int offset)
    {
        _offset = offset;
        return this;
    }

    /// <summary>Append a grouping expression (pipe syntax: <c>| all(group(...)...)</c>)</summary>
    public YqlBuilder GroupBy(GroupingBuilder grouping)
    {
        _grouping = grouping;
        return this;
    }

    /// <summary>Build the final YQL string.</summary>
    public string Build() => Build(includeLimitOffset: true);

    internal string Build(bool includeLimitOffset)
    {
        var sb = new StringBuilder();
        sb.Append("select ");
        sb.Append(_select);
        sb.Append(" from ");
        sb.Append(_from ?? "sources *");

        sb.Append(" where ");
        sb.Append(_where is not null ? _where.Build() : "true");

        if (_orderByClauses.Count > 0)
        {
            sb.Append(" order by ");
            for (var i = 0; i < _orderByClauses.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                var (field, desc) = _orderByClauses[i];
                sb.Append(field);
                sb.Append(desc ? " desc" : " asc");
            }
        }

        if (includeLimitOffset && _limit.HasValue)
        {
            sb.Append(" limit ");
            sb.Append(_limit.Value);
        }

        if (includeLimitOffset && _offset.HasValue)
        {
            sb.Append(" offset ");
            sb.Append(_offset.Value);
        }

        if (_grouping is not null)
        {
            sb.Append(" | ");
            sb.Append(_grouping.Build());
        }

        return sb.ToString();
    }

    internal int? GetLimit() => _limit;
    internal int? GetOffset() => _offset;
    internal string? GetUserQueryText() => _where?.UserQueryText;

    public override string ToString() => Build();
}

/// <summary>
/// Typed variant of <see cref="YqlBuilder"/> returned by <c>From&lt;T&gt;()</c>
/// or created directly via <c>YqlBuilder&lt;T&gt;.Select(...)</c>.
/// Allows lambda-based field resolution without repeating the type argument:
/// <code>
/// // entry point 1 — typed select + auto-inferred From:
/// YqlBuilder&lt;Product&gt;.Select(p => p.Price, p => p.Name).Where(...)
///
/// // entry point 2 — untyped select, typed From:
/// YqlBuilder.Select("*").From&lt;Product&gt;().Where(w => w.Field(p => p.Price).LessThan(100))
/// </code>
/// </summary>
public sealed class YqlBuilder<T> where T : class
{
    private readonly YqlBuilder _inner;

    internal YqlBuilder(YqlBuilder inner) => _inner = inner;

    // ── Static entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Creates a fully-typed query builder selecting the specified fields.
    /// The document type is inferred from <c>[VespaDocument]</c> on <typeparamref name="T"/>.
    /// Pass no arguments (or call <c>Select()</c>) to select all fields (<c>*</c>).
    /// </summary>
    /// <example>
    /// <code>
    /// var yql = YqlBuilder&lt;Product&gt;
    ///     .Select(p => p.Price, p => p.Name)
    ///     .Where(w => w.Field(p => p.Price).LessThan(100))
    ///     .Build();
    /// // → select price, name from product where price &lt; 100;
    /// </code>
    /// </example>
    public static YqlBuilder<T> Select(params Expression<Func<T, object?>>[] fields)
    {
        var fieldList = fields.Length == 0 ? "*" : string.Join(", ", fields.Select(VespaDocumentMeta.FieldName));
        var (docType, _) = VespaDocumentMeta.For<T>();
        return new YqlBuilder<T>(YqlBuilder.Create(fieldList, docType));
    }

    // ── Instance methods ──────────────────────────────────────────────────────

    /// <summary>
    /// Filter with a typed WHERE clause — field names are resolved from
    /// <c>[VespaField]</c> attributes via lambda expressions, no explicit type argument needed.
    /// </summary>
    public YqlBuilder<T> Where(Action<TypedYqlWhereClause<T>> configure)
    {
        _inner.Where(w => configure(new TypedYqlWhereClause<T>(w)));
        return this;
    }

    public YqlBuilder<T> OrderBy(string field, bool descending = false)
    {
        _inner.OrderBy(field, descending);
        return this;
    }

    public YqlBuilder<T> OrderBy(Expression<Func<T, object?>> selector, bool descending = false)
    {
        _inner.OrderBy(VespaDocumentMeta.FieldName(selector), descending);
        return this;
    }

    public YqlBuilder<T> Limit(int limit) { _inner.Limit(limit); return this; }
    public YqlBuilder<T> Offset(int offset) { _inner.Offset(offset); return this; }
    public YqlBuilder<T> GroupBy(GroupingBuilder grouping) { _inner.GroupBy(grouping); return this; }

    internal int? GetLimit() => _inner.GetLimit();
    internal int? GetOffset() => _inner.GetOffset();
    internal string? GetUserQueryText() => _inner.GetUserQueryText();
    internal string Build(bool includeLimitOffset) => _inner.Build(includeLimitOffset);

    public string Build() => _inner.Build();
    public override string ToString() => _inner.ToString();
}
