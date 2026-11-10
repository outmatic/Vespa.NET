using Microsoft.Extensions.AI;

namespace Vespa.VectorData;

/// <summary>Configuration for the provider over existing Vespa schemas.</summary>
public sealed record VespaVectorStoreOptions
{
    /// <summary>Document namespace. Dedicate each connected schema to this namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary>
    /// Deployed profile ranking by cosine similarity or angular closeness, with
    /// <c>match-features: distance(field, embedding)</c> for the mapped vector field.
    /// </summary>
    public required string RankProfile { get; init; }

    /// <summary>Query tensor input declared by the rank profile.</summary>
    public string QueryTensorName { get; init; } = "query_embedding";

    /// <summary>Use HNSW approximate search. Set false for exact search.</summary>
    public bool Approximate { get; init; } = true;

    /// <summary>Extra HNSW exploration hits for approximate search. Null uses Vespa's default.</summary>
    public int? HnswExploreAdditionalHits { get; init; }

    /// <summary>Optional deployed summary for searches/filtered reads without vectors. Null uses Vespa's default summary.</summary>
    public string? SummaryClass { get; init; }

    /// <summary>Deployed summary class used with IncludeVectors=true. Independent of SummaryClass; null uses Vespa's default.</summary>
    public string? VectorSummaryClass { get; init; }

    /// <summary>Mapped storage fields that must have non-null values on reads and writes. C# required data members are also enforced.</summary>
    public IReadOnlyCollection<string> RequiredDataFields { get; init; } = [];

    /// <summary>Maximum records prepared and validated before each group of writes.</summary>
    public int UpsertBatchSize { get; init; } = 64;

    /// <summary>Maximum text inputs per embedding-generation request.</summary>
    public int EmbeddingBatchSize { get; init; } = 32;

    /// <summary>Maximum concurrent upsert requests. Writes for the same key retain input order.</summary>
    public int UpsertConcurrency { get; init; } = 4;

    /// <summary>Maximum top + skip accepted, bounded by the deployed query profile.</summary>
    public int MaxResults { get; init; } = 400;

    /// <summary>Vespa query timeout, independent of the supplied HTTP client's timeout.</summary>
    public TimeSpan SearchTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Optional, caller-owned generator for text queries and dynamic string vector fields.</summary>
    public IEmbeddingGenerator<string, Embedding<float>>? EmbeddingGenerator { get; init; }

    /// <summary>Prefix added to string vector inputs before embedding generation during upsert.</summary>
    public string? DocumentEmbeddingPrefix { get; init; }

    /// <summary>Prefix added to text queries before embedding generation during search.</summary>
    public string? QueryEmbeddingPrefix { get; init; }
}
