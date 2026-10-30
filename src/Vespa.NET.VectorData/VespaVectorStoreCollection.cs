using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Vespa.Models;

namespace Vespa.VectorData;

/// <summary>A typed binding to an existing schema containing one float32 angular vector.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Matches the VectorData VectorStoreCollection abstraction.")]
public sealed class VespaVectorStoreCollection<TKey, TRecord> : VectorStoreCollection<TKey, TRecord> where TKey : notnull where TRecord : class
{
    private readonly IVespaClient _client;
    private readonly VespaVectorStoreOptions _options;
    private readonly VespaVectorRecordMapper<TRecord> _mapper;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;
    private bool _disposed;

    internal VespaVectorStoreCollection(IVespaClient client, string name, VespaVectorStoreOptions options,
        VectorStoreCollectionDefinition? definition)
    {
        if (typeof(TKey) != typeof(string))
            throw new NotSupportedException("The prototype supports string keys only.");
        VespaVectorValidation.Identifier(name, nameof(name));
        _client = client;
        Name = name;
        _options = options;
        _mapper = new(definition);
        var generator = definition?.Properties.OfType<VectorStoreVectorProperty>().Single().EmbeddingGenerator
            ?? definition?.EmbeddingGenerator ?? options.EmbeddingGenerator;
        _embeddingGenerator = generator switch
        {
            null => null,
            IEmbeddingGenerator<string, Embedding<float>> supported => supported,
            _ => throw new NotSupportedException("The configured embedding generator must support string inputs and Embedding<float> outputs.")
        };
    }

    public override string Name { get; }

    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default) =>
        throw VespaVectorValidation.SchemaLifecycle();

    public override Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default) =>
        throw VespaVectorValidation.SchemaLifecycle();

    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default) =>
        throw VespaVectorValidation.SchemaLifecycle();

    public override async Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(record);
        var key = _mapper.GetKey(record);
        var fields = _mapper.ToFields(record);
        var response = await ExecuteAsync(nameof(UpsertAsync), () =>
            _client.Documents.PutAsync(FullId(key), fields, Name, _options.Namespace, cancellationToken: cancellationToken), cancellationToken);
        if (response.HadIgnoredFields)
            throw Failure(nameof(UpsertAsync), "Vespa ignored one or more mapped fields. Check the deployed schema. The write may already have been partially applied; no rollback was performed.");
    }

    public override async Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpsertAsync(record, cancellationToken);
        }
    }

    public override async Task<TRecord?> GetAsync(TKey key, RecordRetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var id = Key(key);
        var document = await ExecuteAsync(nameof(GetAsync), () =>
            _client.Documents.GetAsync<Dictionary<string, JsonElement>>(
                FullId(id), Name, _options.Namespace, cancellationToken: cancellationToken), cancellationToken);
        return document?.Fields is { } fields ? MapFields(id, fields, options?.IncludeVectors == true, nameof(GetAsync)) : null;
    }

    public override async Task DeleteAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var id = Key(key);
        await ExecuteAsync(nameof(DeleteAsync), () =>
            _client.Documents.DeleteAsync(FullId(id), Name, _options.Namespace, cancellationToken: cancellationToken), cancellationToken);
    }

    public override async IAsyncEnumerable<TRecord> GetAsync(Expression<Func<TRecord, bool>> filter, int top,
        FilteredRecordRetrievalOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(filter);
        ValidateWindow(top, options?.Skip ?? 0);
        if (options?.OrderBy is not null)
            throw new NotSupportedException("Ordered filtered retrieval is not supported by this prototype.");
        var request = Request(new VespaVectorFilter<TRecord>(_mapper).Translate(filter), top, options?.Skip ?? 0, options?.IncludeVectors == true);
        foreach (var hit in await QueryAsync(request, nameof(GetAsync), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return MapHit(hit, options?.IncludeVectors == true, nameof(GetAsync));
        }
    }

    public override async IAsyncEnumerable<VectorSearchResult<TRecord>> SearchAsync<TInput>(TInput searchValue, int top,
        VectorSearchOptions<TRecord>? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(searchValue);
        ValidateWindow(top, options?.Skip ?? 0);
        if (options?.ScoreThreshold is { } threshold && !double.IsFinite(threshold))
            throw new ArgumentException("The score threshold must be finite.", nameof(options));
        if (options?.VectorProperty is { } selector)
        {
            var expression = selector.Body is UnaryExpression { NodeType: ExpressionType.Convert } convert ? convert.Operand : selector.Body;
            if (expression is not MemberExpression member || member.Expression != selector.Parameters[0] ||
                member.Member.Name != _mapper.VectorProperty.Property.Name)
                throw new NotSupportedException("Select the collection's mapped vector property directly.");
        }
        var filterPredicate = options?.Filter is { } filter
            ? new VespaVectorFilter<TRecord>(_mapper).Translate(filter) : null;
        var vector = searchValue switch
        {
            ReadOnlyMemory<float> memory => memory.ToArray(),
            float[] array => (float[])array.Clone(),
            string text when _embeddingGenerator is not null => (await _embeddingGenerator.GenerateVectorAsync(text, cancellationToken: cancellationToken)).ToArray(),
            string => throw new NotSupportedException("Text queries require an IEmbeddingGenerator<string, Embedding<float>>."),
            _ => throw new NotSupportedException("Queries must be float[], ReadOnlyMemory<float>, or text with an embedding generator.")
        };
        _mapper.ValidateVector(vector);
        var target = top + (options?.Skip ?? 0);
        var nearest = string.Create(CultureInfo.InvariantCulture,
            $"({{totalTargetHits:{target},approximate:{(_options.Approximate ? "true" : "false")}}}nearestNeighbor({_mapper.VectorProperty.StorageName}, {_options.QueryTensorName}))");
        var predicate = filterPredicate is not null ? $"({filterPredicate}) and {nearest}" : nearest;
        var request = Request(predicate, top, options?.Skip ?? 0, options?.IncludeVectors == true);
        request.Ranking = new RankingConfig { Profile = _options.RankProfile };
        request.Input = new() { [$"query({_options.QueryTensorName})"] = new Dictionary<string, object> { ["values"] = vector } };
        foreach (var hit in await QueryAsync(request, nameof(SearchAsync), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = $"distance(field,{_mapper.VectorProperty.StorageName})";
            if (hit.GetMatchFeature(feature) is not { } distance || !double.IsFinite(distance) || distance < 0 || distance > Math.PI)
                throw Failure(nameof(SearchAsync), $"Rank profile '{_options.RankProfile}' must return match-feature '{feature}' for an angular tensor.");
            var score = Math.Cos(distance);
            if (options?.ScoreThreshold is { } minimum && score < minimum)
                continue;
            yield return new VectorSearchResult<TRecord>(MapHit(hit, options?.IncludeVectors == true, nameof(SearchAsync)), score);
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
            return null;
        if (serviceType == typeof(VectorStoreCollectionMetadata))
            return new VectorStoreCollectionMetadata { VectorStoreSystemName = "vespa", CollectionName = Name };
        return serviceType.IsInstanceOfType(this) ? this : serviceType == typeof(IVespaClient) ? _client : null;
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private VespaSearchRequest Request(string predicate, int top, int skip, bool includeVectors) => new VespaSearchRequest
    {
        Yql = $"select {_mapper.Projection(includeVectors)} from sources * where {predicate}",
        ModelRestrict = Name,
        Hits = top,
        Offset = skip
    }.WithTimeout(_options.SearchTimeout);

    private async Task<List<SearchHit<Dictionary<string, JsonElement>>>> QueryAsync(
        VespaSearchRequest request, string operation, CancellationToken cancellationToken)
    {
        var response = await ExecuteAsync(operation, () =>
            _client.Search.SearchAsync<Dictionary<string, JsonElement>>(request, cancellationToken), cancellationToken);
        if (response.Root.Errors is { Count: > 0 } errors)
            throw Failure(operation, string.Join("; ", errors.Select(e => e.Message)));
        if (response.Root.Coverage is { Full: false })
            throw Failure(operation, "Vespa returned incomplete search coverage.");
        return response.Root.Children;
    }

    private TRecord MapHit(SearchHit<Dictionary<string, JsonElement>> hit, bool includeVectors, string operation)
    {
        var fields = hit.Fields ?? throw Failure(operation, "Vespa returned a search hit without fields.");
        var id = fields.TryGetValue("documentid", out var documentId)
            ? documentId.ValueKind == JsonValueKind.String ? documentId.GetString() : null : hit.FullId;
        if (id is null || !VespaDocumentId.TryParse(id, out var parts) || parts.Namespace != _options.Namespace ||
            parts.DocumentType != Name || parts.Group is not null || parts.Number is not null)
            throw Failure(operation, "Search returned a document outside this collection's namespace/schema or with unsupported group/number addressing. Use a dedicated schema and namespace.");
        return MapFields(parts.UserSpecified, fields, includeVectors, operation);
    }

    private TRecord MapFields(string key, Dictionary<string, JsonElement> fields, bool includeVectors, string operation)
    {
        try
        {
            return _mapper.FromFields(key, fields, includeVectors);
        }
        catch (Exception exception) when (exception is JsonException or VectorStoreException)
        {
            throw Failure(operation, exception.Message, exception);
        }
    }

    private async Task<TResult> ExecuteAsync<TResult>(string operation, Func<Task<TResult>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is VespaException or HttpRequestException or JsonException or OperationCanceledException)
        {
            throw Failure(operation, exception.Message, exception);
        }
    }

    private VectorStoreException Failure(string operation, string message, Exception? innerException = null) => new(message, innerException)
    {
        VectorStoreSystemName = "vespa",
        CollectionName = Name,
        OperationName = operation
    };

    private void ValidateWindow(int top, int skip)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(top);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        if ((long)top + skip > _options.MaxResults)
            throw new ArgumentOutOfRangeException(nameof(top), $"top + skip must not exceed {_options.MaxResults}.");
    }

    private string FullId(string key) => $"id:{_options.Namespace}:{Name}::{key}";

    private static string Key(TKey key)
    {
        var value = (string?)(object?)key;
        ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(key));
        return value;
    }
}
