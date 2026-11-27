using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

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
            ? Deserialize<TValue>(element, options)
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
            value = Deserialize<TValue>(element, options);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "When reflection JSON is disabled, callers must pass generated metadata for the aggregation result.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The reflection-disabled path deserializes with the caller's JsonTypeInfo.")]
    private static TValue? Deserialize<TValue>(JsonElement element, JsonSerializerOptions? options)
    {
        if (VespaJsonReflection.IsEnabled)
            return element.Deserialize<TValue>(options);

        if (options?.TypeInfoResolver is null)
            throw new InvalidOperationException("Pass options with generated JSON metadata to read this grouping aggregation.");

        return element.Deserialize((JsonTypeInfo<TValue>)options.GetTypeInfo(typeof(TValue)));
    }
}
