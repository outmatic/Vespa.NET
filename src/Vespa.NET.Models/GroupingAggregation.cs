using System.Text.Json;

namespace Vespa.Models;

internal static class GroupingAggregation
{
    internal static TValue? Get<TValue>(
        IReadOnlyDictionary<string, JsonElement> aggregations,
        string name,
        JsonSerializerOptions? options)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return aggregations.TryGetValue(name, out var element)
            ? element.Deserialize<TValue>(options)
            : throw new KeyNotFoundException($"Grouping aggregation '{name}' was not returned.");
    }

    internal static bool TryGet<TValue>(
        IReadOnlyDictionary<string, JsonElement> aggregations,
        string name,
        out TValue? value,
        JsonSerializerOptions? options)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        value = default;
        if (!aggregations.TryGetValue(name, out var element))
            return false;

        try
        {
            value = element.Deserialize<TValue>(options);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
