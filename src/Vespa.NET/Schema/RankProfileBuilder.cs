using System.Linq.Expressions;
using Vespa.Models.Attributes;

namespace Vespa.Models.Schema;

/// <summary>Composes Vespa ranking phases and functions for a document schema.</summary>
public sealed class RankProfileBuilder<T> where T : class
{
    private string? _inherits;
    private string? _firstPhase;
    private string? _secondPhase;
    private int _secondPhaseRerankCount;
    private string? _globalPhase;
    private int _globalPhaseRerankCount;
    private readonly Dictionary<string, RankFunctionModel> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _queryInputs = new(StringComparer.Ordinal);

    internal RankProfileBuilder() { }

    /// <summary>Inherits settings from the named rank profile.</summary>
    public RankProfileBuilder<T> Inherits(string profile)
    {
        _inherits = ValidateProfileName(profile, nameof(profile));
        return this;
    }

    /// <summary>Sets the expression used to score all matching documents.</summary>
    public RankProfileBuilder<T> FirstPhase(RankExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        _firstPhase = expression.ToString();
        return this;
    }

    /// <summary>Sets the expression used to rerank top hits on the content nodes.</summary>
    /// <param name="expression">The ranking expression.</param>
    /// <param name="rerankCount">Optional number of hits to rerank on each content node.</param>
    public RankProfileBuilder<T> SecondPhase(RankExpression expression, int? rerankCount = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rerankCount ?? 1, nameof(rerankCount));
        _secondPhase = expression.ToString();
        _secondPhaseRerankCount = rerankCount.GetValueOrDefault();
        return this;
    }

    /// <summary>Sets the expression used to rerank hits after merging the content-node results.</summary>
    /// <param name="expression">The ranking expression.</param>
    /// <param name="rerankCount">Optional number of hits to rerank.</param>
    public RankProfileBuilder<T> GlobalPhase(RankExpression expression, int? rerankCount = null)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rerankCount ?? 1, nameof(rerankCount));
        _globalPhase = expression.ToString();
        _globalPhaseRerankCount = rerankCount.GetValueOrDefault();
        return this;
    }

    /// <summary>Declares a scalar query input with a default that a search request can override.</summary>
    public RankProfileBuilder<T> QueryInput(string name, double defaultValue)
    {
        RankExpression.ValidateIdentifier(name, nameof(name));
        if (!double.IsFinite(defaultValue))
            throw new ArgumentOutOfRangeException(nameof(defaultValue), defaultValue, "Query input defaults must be finite.");
        if (!_queryInputs.TryAdd(name, defaultValue))
            throw new ArgumentException($"Query input '{name}' is already defined.", nameof(name));
        return this;
    }

    /// <summary>Defines a named ranking function with an optional parameter list.</summary>
    public RankProfileBuilder<T> Function(string name, RankExpression expression, params string[] parameters)
    {
        RankExpression.ValidateIdentifier(name, nameof(name));
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(parameters);
        foreach (var parameter in parameters)
            RankExpression.ValidateIdentifier(parameter, nameof(parameters));
        if (parameters.Distinct(StringComparer.Ordinal).Count() != parameters.Length)
            throw new ArgumentException("Function parameter names must be unique.", nameof(parameters));

        var function = new RankFunctionModel($"{name}({string.Join(", ", parameters)})", expression.ToString());
        if (!_functions.TryAdd(name, function))
            throw new ArgumentException($"Ranking function '{name}' is already defined.", nameof(name));
        return this;
    }

    /// <summary>References an attribute field by its model property.</summary>
    public RankExpression Attribute(Expression<Func<T, object?>> selector) =>
        RankExpression.Attribute(VespaDocumentMeta.FieldName(selector));

    /// <summary>Scores an indexed string field by its model property.</summary>
    public RankExpression Bm25(Expression<Func<T, object?>> selector) =>
        RankExpression.Bm25(VespaDocumentMeta.FieldName(selector));

    /// <summary>Scores vector proximity on the field selected by its model property.</summary>
    public RankExpression Closeness(Expression<Func<T, object?>> selector) =>
        RankExpression.Closeness(VespaDocumentMeta.FieldName(selector));

    internal RankProfileDefinition Build(string name) =>
        new(name, _inherits, _firstPhase, _secondPhase, _secondPhaseRerankCount,
            _globalPhase, _globalPhaseRerankCount, _functions.Values.ToArray(), _queryInputs.ToArray());

    internal static string ValidateProfileName(string name, string paramName)
    {
        if (string.IsNullOrEmpty(name) || (!char.IsAsciiLetter(name[0]) && name[0] != '_') ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new ArgumentException($"'{name}' is not a valid rank profile name.", paramName);
        return name;
    }
}
