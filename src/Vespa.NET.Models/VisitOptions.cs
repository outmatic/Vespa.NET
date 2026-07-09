using System.Globalization;

namespace Vespa.Models;

/// <summary>
/// Optional parameters for visit operations (<c>VisitAsync</c>, <c>VisitJsonlAsync</c>,
/// <c>VisitAllAsync</c>) that map to Vespa query parameters on <c>/document/v1</c>.
/// <para><c>dryRun</c> is deliberately not available here: Vespa rejects it on visit
/// requests (HTTP 400).</para>
/// </summary>
public sealed record VisitOptions
{
    /// <summary>
    /// Field set to return (e.g. <c>music:[document]</c>, <c>music:title,artist</c>).
    /// Maps to the <c>fieldSet</c> query parameter.
    /// </summary>
    public string? FieldSet { get; init; }

    /// <summary>
    /// Best-effort lower bound on documents returned per HTTP response before a
    /// continuation token is issued. Maps to the <c>wantedDocumentCount</c> query parameter.
    /// <para>Only sent on paged visits (<c>VisitAsync</c>/<c>VisitAllAsync</c>): JSONL
    /// streaming visits (<c>stream=true</c>) deliver the full result in one streamed
    /// response, which makes this parameter redundant, so it is omitted there.</para>
    /// </summary>
    public int? WantedDocumentCount { get; init; }

    /// <summary>
    /// Vespa server-side soft timeout for each visit request (serialized as <c>{ms}ms</c>).
    /// When the timeout elapses, Vespa returns the progress so far plus a continuation
    /// token; the client resumes automatically. Maps to the <c>timeout</c> query parameter.
    /// </summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>
    /// Total number of slices when splitting a visit across cooperating clients.
    /// Maps to the <c>slices</c> query parameter; use together with <see cref="SliceId"/>.
    /// </summary>
    public int? Slices { get; init; }

    /// <summary>
    /// Zero-based ID of the slice this visit covers (<c>0 ≤ sliceId &lt; slices</c>).
    /// Maps to the <c>sliceId</c> query parameter.
    /// </summary>
    public int? SliceId { get; init; }

    /// <summary>
    /// Number of buckets visited in parallel per request. Maps to the
    /// <c>concurrency</c> query parameter.
    /// </summary>
    public int? Concurrency { get; init; }

    /// <summary>
    /// Only include documents last modified at or after this time point, in
    /// <b>microseconds since UTC epoch</b>. Maps to the <c>fromTimestamp</c> query parameter.
    /// </summary>
    public long? FromTimestamp { get; init; }

    /// <summary>
    /// Only include documents last modified before this time point, in
    /// <b>microseconds since UTC epoch</b>. Maps to the <c>toTimestamp</c> query parameter.
    /// </summary>
    public long? ToTimestamp { get; init; }

    /// <summary>
    /// When <see langword="true"/>, also return tombstones of recently removed documents.
    /// Maps to the <c>includeRemoves</c> query parameter.
    /// </summary>
    public bool? IncludeRemoves { get; init; }

    /// <summary>
    /// Bucket space for the visit (e.g. <c>default</c>, <c>global</c>).
    /// Maps to the <c>bucketSpace</c> query parameter.
    /// </summary>
    public string? BucketSpace { get; init; }

    /// <summary>
    /// Diagnostic trace level (0-9). Maps to the <c>tracelevel</c> query parameter.
    /// </summary>
    public int? TraceLevel { get; init; }

    /// <summary>
    /// Tensor format for response values. Maps to the <c>format.tensors</c> query parameter.
    /// Valid values: <c>short</c>, <c>long</c>, <c>short-value</c>, <c>long-value</c>.
    /// </summary>
    public string? TensorFormat { get; init; }

    /// <summary>
    /// Restrict the visit to one string group, addressing
    /// <c>/document/v1/{namespace}/{type}/group/{group}</c>. Only meaningful for document
    /// types in <c>streaming</c> or <c>store-only</c> mode. Mutually exclusive with
    /// <see cref="Number"/>; not supported on root-level visits (<c>VisitAllAsync</c>).
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Restrict the visit to one numeric bucket, addressing
    /// <c>/document/v1/{namespace}/{type}/number/{number}</c>. Only meaningful for document
    /// types in <c>streaming</c> or <c>store-only</c> mode. Mutually exclusive with
    /// <see cref="Group"/>; not supported on root-level visits (<c>VisitAllAsync</c>).
    /// </summary>
    public long? Number { get; init; }

    /// <summary>
    /// Builds query parameter tuples for use in URL construction.
    /// <see cref="Group"/> and <see cref="Number"/> are NOT surfaced here — they select
    /// the URL path level instead of a query parameter.
    /// </summary>
    /// <param name="includeWantedDocumentCount">
    /// Set to <see langword="false"/> on JSONL streaming visits, where
    /// <c>wantedDocumentCount</c> is redundant and therefore omitted.
    /// </param>
    public IEnumerable<(string Key, string? Value)> ToQueryParams(bool includeWantedDocumentCount = true)
    {
        if (FieldSet is not null)
            yield return ("fieldSet", FieldSet);
        if (includeWantedDocumentCount && WantedDocumentCount.HasValue)
            yield return ("wantedDocumentCount", WantedDocumentCount.Value.ToString(CultureInfo.InvariantCulture));
        if (Timeout.HasValue)
            yield return ("timeout", VespaQueryValues.ToMilliseconds(Timeout.Value));
        if (Slices.HasValue)
            yield return ("slices", Slices.Value.ToString(CultureInfo.InvariantCulture));
        if (SliceId.HasValue)
            yield return ("sliceId", SliceId.Value.ToString(CultureInfo.InvariantCulture));
        if (Concurrency.HasValue)
            yield return ("concurrency", Concurrency.Value.ToString(CultureInfo.InvariantCulture));
        if (FromTimestamp.HasValue)
            yield return ("fromTimestamp", FromTimestamp.Value.ToString(CultureInfo.InvariantCulture));
        if (ToTimestamp.HasValue)
            yield return ("toTimestamp", ToTimestamp.Value.ToString(CultureInfo.InvariantCulture));
        if (IncludeRemoves.HasValue)
            yield return ("includeRemoves", IncludeRemoves.Value ? "true" : "false");
        if (BucketSpace is not null)
            yield return ("bucketSpace", BucketSpace);
        if (TraceLevel.HasValue)
            yield return ("tracelevel", TraceLevel.Value.ToString(CultureInfo.InvariantCulture));
        if (TensorFormat is not null)
            yield return ("format.tensors", TensorFormat);
    }
}
