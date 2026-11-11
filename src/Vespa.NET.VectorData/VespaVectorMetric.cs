using Microsoft.Extensions.VectorData;

namespace Vespa.VectorData;

// Vespa distance(field, ...) is metric-specific, not a generic similarity score.
// https://docs.vespa.ai/en/reference/schemas/schemas.html#distance-metric
internal static class VespaVectorMetric
{
    internal static double Score(string? metric, double distance)
    {
        if (!double.IsFinite(distance))
            throw new ArgumentException("The returned vector distance must be finite.", nameof(distance));
        return (metric ?? DistanceFunction.CosineSimilarity) switch
        {
            DistanceFunction.CosineSimilarity when distance is >= 0 and <= Math.PI => Math.Cos(distance),
            DistanceFunction.CosineDistance when distance is >= 0 and <= Math.PI => 1 - Math.Cos(distance),
            DistanceFunction.DotProductSimilarity => -distance,
            DistanceFunction.NegativeDotProductSimilarity => distance,
            DistanceFunction.EuclideanDistance when distance >= 0 => distance,
            DistanceFunction.EuclideanSquaredDistance when distance >= 0 && distance <= Math.Sqrt(double.MaxValue) => distance * distance,
            _ => throw new ArgumentException("The returned vector distance is incompatible with the configured metric.", nameof(distance))
        };
    }

    internal static bool PassesThreshold(string? metric, double score, double threshold) =>
        metric is null or DistanceFunction.CosineSimilarity or DistanceFunction.DotProductSimilarity
            ? score >= threshold : score <= threshold;

    // Dot-product distance has a sign-only round trip through Vespa's raw score.
    // Expand by one representable value to preserve inclusive client thresholds
    // even when a backend distance check is strict. Other metrics reconstruct
    // distance through closeness; a direct inverse can exclude valid boundary hits.
    internal static double? DistanceThreshold(string? metric, double? scoreThreshold)
    {
        if (scoreThreshold is not { } threshold)
            return null;
        var distance = metric switch
        {
            DistanceFunction.DotProductSimilarity => -threshold,
            DistanceFunction.NegativeDotProductSimilarity => threshold,
            _ => double.NaN
        };
        var bound = Math.BitIncrement(distance);
        return double.IsFinite(bound) ? bound : null;
    }
}
