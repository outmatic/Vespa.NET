using System.Text.Json.Serialization;

namespace Vespa.Models;

/// <summary>
/// Top-level response from <c>/state/v1/metrics</c>.
/// </summary>
public sealed record VespaMetrics(
    [property: JsonPropertyName("time")] long Time,
    [property: JsonPropertyName("status")] VespaStatus Status,
    [property: JsonPropertyName("metrics")] VespaMetricsData? Metrics
);

/// <summary>
/// Status node inside the metrics response.
/// </summary>
public sealed record VespaStatus(
    [property: JsonPropertyName("code")] string Code
);

/// <summary>
/// Container for metric snapshot and values.
/// </summary>
public sealed record VespaMetricsData(
    [property: JsonPropertyName("snapshot")] MetricsSnapshot Snapshot,
    [property: JsonPropertyName("values")] IReadOnlyList<MetricValue> Values
);

/// <summary>
/// Time window of the metrics snapshot.
/// </summary>
public sealed record MetricsSnapshot(
    [property: JsonPropertyName("from")] double From,
    [property: JsonPropertyName("to")] double To
);

/// <summary>
/// A single named metric with aggregated values and optional dimensions.
/// </summary>
public sealed record MetricValue(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("values")] MetricValues Values,
    [property: JsonPropertyName("dimensions")] IReadOnlyDictionary<string, string>? Dimensions
);

/// <summary>
/// Aggregated numeric values for a metric within the snapshot window.
/// </summary>
public sealed record MetricValues(
    [property: JsonPropertyName("last")] double? Last,
    [property: JsonPropertyName("max")] double? Max,
    [property: JsonPropertyName("sum")] double? Sum,
    [property: JsonPropertyName("count")] long? Count
);
