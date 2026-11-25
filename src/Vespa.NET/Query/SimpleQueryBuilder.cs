using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Vespa.Models.Attributes;

namespace Vespa.Query;

/// <summary>Composes words and phrases in Vespa's Simple Query Language.</summary>
public sealed class SimpleQueryBuilder
{
    private readonly List<string> _terms = [];

    /// <summary>Adds one word, optionally scoped to a search field or fieldset.</summary>
    public SimpleQueryBuilder Word(
        string text, SimpleQueryOccurrence occurrence = SimpleQueryOccurrence.Default, string? field = null) =>
        Add(text, occurrence, field, phrase: false);

    /// <summary>Adds one word scoped to the mapped name of a model property.</summary>
    public SimpleQueryBuilder Word<T>(
        Expression<Func<T, object?>> field, string text, SimpleQueryOccurrence occurrence = SimpleQueryOccurrence.Default) =>
        Word(text, occurrence, VespaDocumentMeta.FieldName(field));

    /// <summary>Adds a quoted phrase, optionally scoped to a search field or fieldset.</summary>
    public SimpleQueryBuilder Phrase(
        string text, SimpleQueryOccurrence occurrence = SimpleQueryOccurrence.Default, string? field = null) =>
        Add(text, occurrence, field, phrase: true);

    /// <summary>Adds a quoted phrase scoped to the mapped name of a model property.</summary>
    public SimpleQueryBuilder Phrase<T>(
        Expression<Func<T, object?>> field, string text, SimpleQueryOccurrence occurrence = SimpleQueryOccurrence.Default) =>
        Phrase(text, occurrence, VespaDocumentMeta.FieldName(field));

    /// <summary>Returns the composed Simple Query Language string.</summary>
    public string Build()
    {
        if (_terms.Count == 0)
            throw new InvalidOperationException("Add a word or phrase before building the query.");
        return string.Join(' ', _terms);
    }

    internal string BuildRecall()
    {
        if (_terms.Exists(term => term[0] is not ('+' or '-')))
            throw new InvalidOperationException("Every recall word or phrase must be required or excluded.");

        return Build();
    }

    private SimpleQueryBuilder Add(string text, SimpleQueryOccurrence occurrence, string? field, bool phrase)
    {
        var prefix = occurrence switch
        {
            SimpleQueryOccurrence.Default => "",
            SimpleQueryOccurrence.Required => "+",
            SimpleQueryOccurrence.Excluded => "-",
            _ => throw new ArgumentOutOfRangeException(nameof(occurrence), occurrence, "Expected a valid term occurrence.")
        };

        if (field is not null && (field.Contains('{') || !YqlIdentifier.IsValid(field)))
            throw new ArgumentException("Use a field or fieldset name with simple identifier segments.", nameof(field));

        ArgumentNullException.ThrowIfNull(text);
        string value;
        if (phrase)
        {
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || !words.All(IsWord))
                throw new ArgumentException("A phrase must contain words separated by whitespace.", nameof(text));
            value = $"\"{string.Join(' ', words)}\"";
        }
        else
        {
            if (!IsWord(text))
                throw new ArgumentException("A word must contain only letters, combining marks, or digits.", nameof(text));
            value = text;
        }

        _terms.Add(field is null ? prefix + value : $"{prefix}{field}:{value}");
        return this;
    }

    private static bool IsWord(string value)
    {
        if (value.Length == 0 || !Rune.TryGetRuneAt(value, 0, out var first) || !Rune.IsLetterOrDigit(first))
            return false;

        foreach (var rune in value.EnumerateRunes())
            if (!Rune.IsLetterOrDigit(rune) && Rune.GetUnicodeCategory(rune) is not
                (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                return false;

        return true;
    }
}
