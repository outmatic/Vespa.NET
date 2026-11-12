using Microsoft.Extensions.AI;

namespace Vespa.VectorData;

/// <summary>Configuration for the provider over existing Vespa schemas.</summary>
public sealed record VespaVectorStoreOptions
{
    /// <summary>Optional authoritative, read-only collection catalog. Does not deploy or remove schemas.</summary>
    public IVespaCollectionCatalog? CollectionCatalog { get; init; }

    /// <summary>
    /// Explicit application assertion that schemas were deployed externally. Allows EnsureCollectionExistsAsync
    /// to validate searchability without a catalog; this is not live schema discovery or automatic creation.
    /// </summary>
    public bool AssumeSchemaExists { get; init; }

    /// <summary>Document namespace. Dedicate each connected schema to this namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary>
    /// Deployed profile ranking monotonically by vector proximity, with
    /// <c>match-features: distance(field, embedding)</c> for the mapped vector field.
    /// Cosine metrics require angular; dot-product metrics require dotproduct;
    /// Euclidean metrics require euclidean in the deployed schema.
    /// </summary>
    public string? RankProfile { get; init; }

    /// <summary>Query tensor input declared by the rank profile.</summary>
    public string QueryTensorName { get; init; } = "query_embedding";

    /// <summary>Per-vector bindings keyed by CLR/definition property name. Collections with multiple vectors require explicit rank profile and query tensor bindings.</summary>
    public IReadOnlyDictionary<string, VespaVectorFieldOptions> VectorFields { get; init; } = new Dictionary<string, VespaVectorFieldOptions>();

    /// <summary>Deployed hybrid profile combining lexical and vector relevance. Its relevance is the hybrid result score; larger is better.</summary>
    public string? HybridRankProfile { get; init; }

    /// <summary>Dense candidate target for hybrid retrieval. Null uses top + skip; smaller configured values are raised to that window. Tune independently of the keyword target and coordinate with the deployed reranking budget.</summary>
    public int? HybridVectorTargetHits { get; init; }

    /// <summary>Lexical candidate target for hybrid retrieval. Null uses top + skip; smaller configured values are raised to that window. Tune independently of the vector target; the default is not a recall or latency optimum.</summary>
    public int? HybridKeywordTargetHits { get; init; }

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

/// <summary>Existing Vespa query bindings for one vector property. No schema deployment is performed.</summary>
public sealed record VespaVectorFieldOptions
{
    /// <summary>Profile ordering hits by this vector's proximity and returning its distance match feature.</summary>
    public string? RankProfile { get; init; }

    /// <summary>Query tensor input matching this vector's dimension name, size, and cell type.</summary>
    public string? QueryTensorName { get; init; }

    /// <summary>Optional hybrid profile override for this vector.</summary>
    public string? HybridRankProfile { get; init; }
}
