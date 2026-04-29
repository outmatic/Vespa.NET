using System.Diagnostics;
using System.Reflection;

namespace Vespa;

/// <summary>
/// OpenTelemetry instrumentation for Vespa.NET.
/// Register the listener in your OpenTelemetry setup:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(VespaActivitySource.Name));
/// </code>
/// </summary>
public static class VespaActivitySource
{
    /// <summary>The ActivitySource name: <c>"Vespa.NET"</c></summary>
    public const string Name = "Vespa.NET";

    internal static readonly ActivitySource Instance = new(
        Name,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

    // ── Span names ──────────────────────────────────────────────────────────────

    internal const string Search = "vespa.search";
    internal const string SearchStream = "vespa.search.stream";
    internal const string SearchGroup = "vespa.search.group";
    internal const string NearestNeighbor = "vespa.search.nearest_neighbor";
    internal const string DocumentGet = "vespa.document.get";
    internal const string DocumentPut = "vespa.document.put";
    internal const string DocumentDelete = "vespa.document.delete";
    internal const string DocumentUpdate = "vespa.document.update";
    internal const string FeedPipeline = "vespa.feed.pipeline";

    // ── Tag names (follow OpenTelemetry semantic conventions where applicable) ──

    internal const string TagYql = "vespa.yql";
    internal const string TagDocType = "vespa.document_type";
    internal const string TagNamespace = "vespa.namespace";
    internal const string TagDocId = "vespa.document_id";
    internal const string TagHits = "vespa.hits";
    internal const string TagTotalCount = "vespa.total_count";
    internal const string TagTopK = "vespa.top_k";
    internal const string TagEmbeddingField = "vespa.embedding_field";
    internal const string TagFeedCount = "vespa.feed.count";
    internal const string TagFeedSuccess = "vespa.feed.success_count";
}
