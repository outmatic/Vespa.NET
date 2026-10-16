using System.Text.Json;
using Vespa.Models;

namespace Vespa.Search;

/// <summary>Parses grouping trees and metadata independently of HTTP execution.</summary>
internal static class GroupingResponseParser
{
    internal static GroupingSearchResponse<T> Parse<T>(JsonElement rootEl) where T : class
    {
        TimingInfo? timing = rootEl.TryGetProperty("timing", out var timingEl)
            ? timingEl.Deserialize<TimingInfo>(VespaJsonOptions.Default)
            : null;

        TraceInfo? trace = rootEl.TryGetProperty("trace", out var traceEl)
            ? traceEl.Deserialize<TraceInfo>(VespaJsonOptions.Default)
            : null;

        if (!rootEl.TryGetProperty("root", out var searchRoot))
            throw new VespaException("Search response does not contain a root object.");

        Coverage? coverage = searchRoot.TryGetProperty("coverage", out var coverageEl)
            ? coverageEl.Deserialize<Coverage>(VespaJsonOptions.Default)
            : null;
        List<VespaError>? errors = searchRoot.TryGetProperty("errors", out var errorsEl)
            ? errorsEl.Deserialize<List<VespaError>>(VespaJsonOptions.Default)
            : null;

        long totalCount = searchRoot.TryGetProperty("fields", out var fields) &&
            fields.TryGetProperty("totalCount", out var tc)
                ? tc.GetInt64()
                : 0;

        var hits = new List<SearchHit<T>>();
        var groupingResults = new List<VespaGroupList>();
        var groupingRoots = new List<VespaGroup>();
        IReadOnlyList<string>? continuationTokens = null;

        if (searchRoot.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                if (!child.TryGetProperty("id", out var idEl)) continue;
                var id = idEl.GetString() ?? "";

                if (id.StartsWith("group:root:", StringComparison.Ordinal))
                {
                    var groupRoot = ParseGroup<T>(child);
                    groupingRoots.Add(groupRoot);
                    groupingResults.AddRange(groupRoot.SubGroups);
                    continuationTokens ??= ExtractContinuationTokens(child);
                }
                else
                {
                    var hit = child.Deserialize<SearchHit<T>>(VespaJsonOptions.Default);
                    if (hit is not null)
                    {
                        VespaIdInjector.Inject(hit);
                        hits.Add(hit);
                    }
                }
            }
        }

        return new GroupingSearchResponse<T>(hits, groupingResults, totalCount, timing, continuationTokens)
        {
            GroupingRoots = groupingRoots,
            Coverage = coverage,
            Errors = errors,
            Trace = trace
        };
    }

    /// <summary>
    /// Collects grouping continuation tokens from a <c>group:root</c> node:
    /// the root's <c>continuation.this</c> first, then every group list's
    /// <c>continuation.next</c> (recursively). Returns <see langword="null"/> when
    /// no list has a next page.
    /// </summary>
    private static List<string>? ExtractContinuationTokens(JsonElement groupRootNode)
    {
        var nextTokens = new List<string>();
        CollectNextTokens(groupRootNode, nextTokens);
        if (nextTokens.Count == 0)
            return null;

        var tokens = new List<string>();
        // Vespa renders "this" as an empty string on the first page — skip it
        if (groupRootNode.TryGetProperty("continuation", out var contEl) &&
            contEl.TryGetProperty("this", out var thisEl) &&
            thisEl.GetString() is { Length: > 0 } thisToken)
            tokens.Add(thisToken);
        tokens.AddRange(nextTokens);
        return tokens;
    }

    private static void CollectNextTokens(JsonElement node, List<string> output)
    {
        if (node.TryGetProperty("id", out var idEl) &&
            (idEl.GetString() ?? "").StartsWith("grouplist:", StringComparison.Ordinal) &&
            node.TryGetProperty("continuation", out var contEl) &&
            contEl.TryGetProperty("next", out var nextEl) &&
            nextEl.GetString() is { Length: > 0 } token)
            output.Add(token);

        if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            foreach (var child in children.EnumerateArray())
                CollectNextTokens(child, output);
    }

    private static VespaGroupList ParseSingleGroupList<T>(JsonElement groupListNode, string label) where T : class
    {
        var groups = new List<VespaGroup>();

        if (groupListNode.TryGetProperty("children", out var groupChildren))
            foreach (var groupNode in groupChildren.EnumerateArray())
                groups.Add(ParseGroup<T>(groupNode));

        return new VespaGroupList(label, groups);
    }

    private static VespaGroup ParseGroup<T>(JsonElement node) where T : class
    {
        var id = node.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        var value = node.TryGetProperty("value", out var valueEl) ? valueEl.ToString() : ParseBucketId(id);
        var aggregations = new Dictionary<string, double>();
        var rawAggregations = new Dictionary<string, JsonElement>();

        if (node.TryGetProperty("fields", out var fields))
            foreach (var field in fields.EnumerateObject())
            {
                // Clones must outlive the response JsonDocument.
                rawAggregations[field.Name] = field.Value.Clone();
                if (field.Value.ValueKind == JsonValueKind.Number && field.Value.TryGetDouble(out var number))
                    aggregations[field.Name] = number;
            }

        var subGroups = (IReadOnlyList<VespaGroupList>)[];
        var hits = (IReadOnlyList<object>)[];
        if (node.TryGetProperty("children", out var children))
            (subGroups, hits) = ParseGroupChildren<T>(children);

        return new VespaGroup(value, aggregations, subGroups)
        {
            Id = id,
            Hits = hits,
            RawAggregations = rawAggregations
        };
    }

    /// <summary>
    /// A group node's children mix nested groupings (<c>grouplist:*</c>) with
    /// per-group document summaries (<c>hitlist:*</c>, from <c>each(output(summary()))</c>).
    /// </summary>
    private static (IReadOnlyList<VespaGroupList> SubGroups, IReadOnlyList<object> Hits) ParseGroupChildren<T>(
        JsonElement childrenEl) where T : class
    {
        var subGroups = new List<VespaGroupList>();
        var hits = new List<object>();

        foreach (var child in childrenEl.EnumerateArray())
        {
            if (!child.TryGetProperty("id", out var idEl)) continue;
            var id = idEl.GetString() ?? "";

            if (id.StartsWith("grouplist:", StringComparison.Ordinal))
            {
                subGroups.Add(ParseSingleGroupList<T>(child, id["grouplist:".Length..]));
            }
            else if (id.StartsWith("hitlist:", StringComparison.Ordinal) && child.TryGetProperty("children", out var hitChildren))
            {
                foreach (var hitNode in hitChildren.EnumerateArray())
                {
                    var hit = hitNode.Deserialize<SearchHit<T>>(VespaJsonOptions.Default);
                    if (hit is null) continue;
                    VespaIdInjector.Inject(hit);
                    hits.Add(hit);
                }
            }
        }

        return (subGroups, hits);
    }

    /// <summary>
    /// Extracts "from:to" from bucket group IDs like "group:long_bucket:0:100"
    /// or "group:double_bucket:0.0:99.9" or "group:string_bucket:a:m".
    /// </summary>
    private static string ParseBucketId(string groupId)
    {
        // Format: "group:{type}_bucket:{from}:{to}"
        const string bucketMarker = "_bucket:";
        var bucketIdx = groupId.IndexOf(bucketMarker, StringComparison.Ordinal);
        if (bucketIdx < 0)
            return groupId;

        var rangeStart = bucketIdx + bucketMarker.Length;
        if (rangeStart >= groupId.Length)
            return groupId;

        // The range portion is "from:to" — return as-is
        return groupId[rangeStart..];
    }
}
