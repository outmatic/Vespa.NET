using Microsoft.Extensions.VectorData;

namespace Vespa.VectorData;

/// <summary>
/// A VectorData provider for pre-deployed Vespa schemas. The supplied client and
/// embedding generator remain owned by the caller. Schema deployment is external.
/// </summary>
public sealed class VespaVectorStore : VectorStore
{
    private readonly IVespaClient _client;
    private readonly VespaVectorStoreOptions _options;
    private bool _disposed;

    public VespaVectorStore(IVespaClient client, VespaVectorStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _options = ValidateAndSnapshotOptions(options);
    }

    public override VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name, VectorStoreCollectionDefinition? definition = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new VespaVectorStoreCollection<TKey, TRecord>(_client, name, _options, definition);
    }

    /// <summary>Binds a collection with its own complete options, replacing the store defaults.</summary>
    public VectorStoreCollection<TKey, TRecord> GetCollection<TKey, TRecord>(
        string name, VectorStoreCollectionDefinition? definition, VespaVectorStoreOptions options)
        where TKey : notnull where TRecord : class
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new VespaVectorStoreCollection<TKey, TRecord>(_client, name, ValidateAndSnapshotOptions(options), definition);
    }

    public override VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(
        string name, VectorStoreCollectionDefinition definition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(definition);
        return new VespaVectorStoreCollection<object, Dictionary<string, object?>>(_client, name, _options, definition);
    }

    /// <summary>Binds a dynamic collection with its own complete options, replacing the store defaults.</summary>
    public VectorStoreCollection<object, Dictionary<string, object?>> GetDynamicCollection(
        string name, VectorStoreCollectionDefinition definition, VespaVectorStoreOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(definition);
        return new VespaVectorStoreCollection<object, Dictionary<string, object?>>(_client, name, ValidateAndSnapshotOptions(options), definition);
    }

    public override IAsyncEnumerable<string> ListCollectionNamesAsync(CancellationToken cancellationToken = default) =>
        throw VespaVectorValidation.SchemaLifecycle();

    public override Task<bool> CollectionExistsAsync(string name, CancellationToken cancellationToken = default) =>
        throw VespaVectorValidation.SchemaLifecycle();

    public override Task EnsureCollectionDeletedAsync(string name, CancellationToken cancellationToken = default) =>
        throw VespaVectorValidation.SchemaLifecycle();

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
            return null;
        if (serviceType == typeof(VectorStoreMetadata))
            return new VectorStoreMetadata { VectorStoreSystemName = "vespa" };
        return serviceType.IsInstanceOfType(this) ? this
            : serviceType == typeof(IVespaClient) ? _client : null;
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private static VespaVectorStoreOptions ValidateAndSnapshotOptions(VespaVectorStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Namespace);
        if (options.Namespace.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("A document namespace cannot contain ':'.", nameof(options));
        VespaVectorValidation.Identifier(options.RankProfile, nameof(options.RankProfile));
        VespaVectorValidation.Identifier(options.QueryTensorName, nameof(options.QueryTensorName));
        if (options.SummaryClass is { } summary)
            ArgumentException.ThrowIfNullOrWhiteSpace(summary, nameof(options.SummaryClass));
        if (options.VectorSummaryClass is { } vectorSummary)
            ArgumentException.ThrowIfNullOrWhiteSpace(vectorSummary, nameof(options.VectorSummaryClass));
        if (options.HnswExploreAdditionalHits is { } exploration)
            ArgumentOutOfRangeException.ThrowIfNegative(exploration, nameof(options.HnswExploreAdditionalHits));
        ArgumentNullException.ThrowIfNull(options.RequiredDataFields);
        foreach (var field in options.RequiredDataFields)
            VespaVectorValidation.Identifier(field, nameof(options.RequiredDataFields));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.UpsertBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.EmbeddingBatchSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.UpsertConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxResults);
        if (options.SearchTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "SearchTimeout must be positive.");
        return options with { RequiredDataFields = options.RequiredDataFields.ToArray() };
    }
}

internal static class VespaVectorValidation
{
    internal static void Identifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("Expected a simple Vespa identifier containing letters, digits or underscores.", parameterName);
    }

    internal static NotSupportedException SchemaLifecycle() => new(
        "Collection discovery and schema creation/deletion require application deployment. " +
        "Deploy the complete Vespa application separately; this provider connects to existing schemas.");
}
