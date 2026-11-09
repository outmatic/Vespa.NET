# Vespa.NET.VectorData changelog

This package is versioned independently of the base Vespa.NET SDK.

## 1.0.0

- Typed `Microsoft.Extensions.VectorData` collections over existing Vespa schemas,
  with caller-assigned string keys and one dense float vector per record.
- Record upsert, key-based reads and deletes, filtered retrieval, and vector
  search with cosine scores, paging and scalar filters.
- Text queries through a caller-owned `IEmbeddingGenerator`; applications generate
  document embeddings before writing records.
- Explicit failures for unsupported schema lifecycle operations, mappings and
  filters, with contextual errors for backend failures.
- Microsoft Agent Framework examples for documentation retrieval and a user
  preference remembered across separate runs, with Ollama for chat and embeddings.
- .NET 8 and .NET 10 targets, depending on Vespa.NET 3.1.1 or later. Agent Framework
  remains a sample dependency, and the connector adds no embedding-provider dependency.

See the [package README](README.md) for schema requirements, supported operations
and limitations.
