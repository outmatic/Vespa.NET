using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Vespa.Models;

namespace Vespa.VectorData;

/// <summary>A record binding to an existing Vespa schema, with dense vector and keyword hybrid search.</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Matches the VectorData VectorStoreCollection abstraction.")]
public sealed class VespaVectorStoreCollection<TKey, TRecord> : VectorStoreCollection<TKey, TRecord>, IKeywordHybridSearchable<TRecord> where TKey : notnull where TRecord : class
{
    private readonly IVespaClient _client;
    private readonly VespaVectorStoreOptions _options;
    private readonly VespaVectorRecordMapper<TRecord> _mapper;
    private bool _disposed;

    internal VespaVectorStoreCollection(IVespaClient client, string name, VespaVectorStoreOptions options,
        VectorStoreCollectionDefinition? definition)
    {
        VespaVectorValidation.Identifier(name, nameof(name));
        _client = client;
        Name = name;
        _options = options;
        _mapper = new(definition, options.RequiredDataFields, options.EmbeddingGenerator);
        if (typeof(TKey) != _mapper.KeyType && !(typeof(TKey) == typeof(object) && _mapper.IsDynamic))
            throw new NotSupportedException("The collection key type must match the mapped key type; dynamic collections use object keys.");
        foreach (var propertyName in options.VectorFields.Keys)
            if (!_mapper.Property(propertyName).IsVector)
                throw new ArgumentException($"Vector binding '{propertyName}' must identify a mapped vector property.", nameof(options));
    }

    public override string Name { get; }

    public override Task<bool> CollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return VespaCollectionProbe.ExistsAsync(Name, _options.CollectionCatalog, cancellationToken);
    }

    public override async Task EnsureCollectionExistsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.CollectionCatalog is not null)
        {
            if (!await CollectionExistsAsync(cancellationToken))
                throw Failure(nameof(EnsureCollectionExistsAsync),
                    $"Schema '{Name}' is not deployed. Deploy the Vespa application separately before using this collection; automatic schema deployment is not enabled by this provider.");
        }
        else if (!_options.AssumeSchemaExists)
            throw new NotSupportedException("EnsureCollectionExistsAsync requires a CollectionCatalog or the explicit AssumeSchemaExists assertion for externally deployed schemas. This provider never creates schemas automatically.");
        await VespaCollectionProbe.ValidateSearchabilityAsync(_client, Name, _options.SearchTimeout,
            nameof(EnsureCollectionExistsAsync), cancellationToken);
    }

    /// <summary>
    /// Checks that the configured source can be queried. Search-chain aliases may resolve too;
    /// this does not certify schema existence or validate its mapping and ranking contract.
    /// </summary>
    public Task ValidateSearchabilityAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return VespaCollectionProbe.ValidateSearchabilityAsync(_client, Name, _options.SearchTimeout,
            nameof(ValidateSearchabilityAsync), cancellationToken);
    }

    public override Task EnsureCollectionDeletedAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        throw VespaVectorValidation.SchemaLifecycle();
    }

    public override async Task UpsertAsync(TRecord record, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfUpsertCanceled(cancellationToken);
        ArgumentNullException.ThrowIfNull(record);
        await UpsertAsync([record], cancellationToken);
    }

    public override async Task UpsertAsync(IEnumerable<TRecord> records, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfUpsertCanceled(cancellationToken);
        ArgumentNullException.ThrowIfNull(records);
        List<VespaVectorUpsertOutcome> completed = [];
        List<PendingDocument> chunk = [];
        var inputFullyEnumerated = false;
        try
        {
            using var input = records.GetEnumerator();
            while (true)
            {
                chunk.Clear();
                while (chunk.Count < _options.UpsertBatchSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!input.MoveNext())
                    {
                        inputFullyEnumerated = true;
                        break;
                    }
                    var document = new PendingDocument(completed.Count + (long)chunk.Count);
                    chunk.Add(document);
                    var record = input.Current;
                    ArgumentNullException.ThrowIfNull(record);
                    document.Key = _mapper.GetKey(record);
                    document.Fields = _mapper.ToFields(record);
                }
                if (chunk.Count == 0)
                    return;
                await GenerateEmbeddingsAsync(chunk, cancellationToken);
                await WriteChunkAsync(chunk, cancellationToken);
                var error = chunk.Select(d => d.Outcome).FirstOrDefault(o => o.Status == VespaVectorUpsertStatus.Unconfirmed)?.Error;
                completed.AddRange(chunk.Select(d => d.Outcome));
                chunk.Clear();
                cancellationToken.ThrowIfCancellationRequested();
                if (error is not null)
                    throw new VespaVectorUpsertException(Name, completed, inputFullyEnumerated, error);
                if (inputFullyEnumerated)
                    return;
            }
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new VespaVectorUpsertCanceledException([.. completed, .. chunk.Select(d => d.Outcome)],
                inputFullyEnumerated, exception, cancellationToken);
        }
        catch (Exception exception) when (exception is not VespaVectorUpsertException &&
            completed.Any(o => o.Status != VespaVectorUpsertStatus.NotAttempted))
        {
            throw new VespaVectorUpsertException(Name, [.. completed, .. chunk.Select(d => d.Outcome)], inputFullyEnumerated, exception);
        }
    }

    private async Task GenerateEmbeddingsAsync(List<PendingDocument> chunk, CancellationToken cancellationToken)
    {
        foreach (var property in _mapper.VectorProperties)
        {
            var pending = chunk.Where(d => d.Fields!.TryGetValue(property.StorageName, out var value) && value is string).ToArray();
            if (pending.Length == 0)
                continue;
            var generator = GetEmbeddingGenerator(property)
                ?? throw new NotSupportedException($"Upserting string vector field '{property.Name}' requires an IEmbeddingGenerator<string, Embedding<float>>.");
            foreach (var batch in pending.Chunk(_options.EmbeddingBatchSize))
            {
                var texts = batch.Select(d => _options.DocumentEmbeddingPrefix + (string)d.Fields![property.StorageName]!);
                var embeddings = await generator.GenerateAsync(texts, cancellationToken: cancellationToken);
                if (embeddings.Count != batch.Length)
                    throw Failure(nameof(UpsertAsync), "The embedding generator returned a different number of vectors than input records.");
                for (var i = 0; i < batch.Length; i++)
                {
                    var vector = embeddings[i].Vector.ToArray();
                    try
                    {
                        VespaVectorRecordMapper<TRecord>.ValidateVector(property, vector);
                    }
                    catch (ArgumentException exception)
                    {
                        throw Failure(nameof(UpsertAsync), $"The embedding generator returned an invalid vector for '{property.Name}'.", exception);
                    }
                    batch[i].Fields![property.StorageName] = VespaVectorRecordMapper<TRecord>.Tensor(vector);
                }
            }
        }
    }

    private Task WriteChunkAsync(List<PendingDocument> chunk, CancellationToken cancellationToken) =>
        Parallel.ForEachAsync(chunk.GroupBy(d => d.Key, StringComparer.Ordinal),
            new ParallelOptions { MaxDegreeOfParallelism = _options.UpsertConcurrency }, async (group, _) =>
            {
                foreach (var document in group)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    try
                    {
                        var response = await ExecuteAsync(nameof(UpsertAsync), () =>
                            _client.Documents.PutAsync(FullId(document.Key!), document.Fields!, Name, _options.Namespace,
                                cancellationToken: cancellationToken), cancellationToken);
                        if (response.HadIgnoredFields)
                            throw Failure(nameof(UpsertAsync), "Vespa ignored mapped fields. Check the deployed schema; the write may have been partially applied.");
                        document.Outcome = document.Outcome with { Status = VespaVectorUpsertStatus.Succeeded };
                    }
                    catch (Exception exception)
                    {
                        document.Outcome = document.Outcome with { Status = VespaVectorUpsertStatus.Unconfirmed, Error = exception };
                        return;
                    }
                }
            });

    private sealed class PendingDocument(long index)
    {
        internal string? Key
        {
            get => Outcome.Key;
            set => Outcome = Outcome with { Key = value };
        }
        internal Dictionary<string, object?>? Fields { get; set; }
        internal VespaVectorUpsertOutcome Outcome { get; set; } = new(index, null, VespaVectorUpsertStatus.NotAttempted);
    }

    private static void ThrowIfUpsertCanceled(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            throw new VespaVectorUpsertCanceledException([], false, new OperationCanceledException(cancellationToken), cancellationToken);
    }

    public override async Task<TRecord?> GetAsync(TKey key, RecordRetrievalOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var id = _mapper.FormatKey(key);
        var document = await ExecuteAsync(nameof(GetAsync), () =>
            _client.Documents.GetAsync<Dictionary<string, JsonElement>>(
                FullId(id), Name, _options.Namespace, fieldSet: _mapper.FieldSet(Name, options?.IncludeVectors == true),
                cancellationToken: cancellationToken), cancellationToken);
        return document is null ? null : MapFields(id, document.Fields ?? [], options?.IncludeVectors == true, nameof(GetAsync), "document field set");
    }

    public override async Task DeleteAsync(TKey key, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var id = _mapper.FormatKey(key);
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
        var translator = new VespaVectorFilter<TRecord>(_mapper);
        var request = Request(translator.Translate(filter), top, options?.Skip ?? 0, options?.IncludeVectors == true);
        request.Ranking = new RankingConfig { Profile = "unranked" };
        if (options?.OrderBy is { } orderBy)
        {
            var sort = orderBy(new()).Values.Select(item =>
            {
                var property = translator.ResolvePropertySelector(item.PropertySelector);
                if (!property.IsFilterable || property.IsVector || VespaCollectionModel.IsCollectionType(property.Type))
                    throw new NotSupportedException("Ordering requires a scalar data property marked IsIndexed and backed by a Vespa attribute.");
                return $"{property.StorageName} {(item.Ascending ? "asc" : "desc")}";
            }).ToArray();
            if (sort.Length > 0)
            {
                request.Yql += " order by " + string.Join(", ", sort);
            }
        }
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
        ValidateThreshold(options?.ScoreThreshold);
        var property = SelectVector(options?.VectorProperty);
        var binding = Binding(property, hybrid: false);
        var filterPredicate = options?.Filter is { } filter
            ? new VespaVectorFilter<TRecord>(_mapper).Translate(filter) : null;
        var vector = await QueryVectorAsync(searchValue, property, cancellationToken);
        var nearest = Nearest(property, binding.QueryTensorName!, top + (options?.Skip ?? 0),
            VespaVectorMetric.DistanceThreshold(property.DistanceFunction, options?.ScoreThreshold));
        var predicate = filterPredicate is not null ? $"({filterPredicate}) and {nearest}" : nearest;
        var request = Request(predicate, top, options?.Skip ?? 0, options?.IncludeVectors == true);
        request.Ranking = new RankingConfig { Profile = binding.RankProfile };
        request.Input = new() { [$"query({binding.QueryTensorName})"] = VespaVectorRecordMapper<TRecord>.Tensor(vector) };
        var previousDistance = double.NegativeInfinity;
        foreach (var hit in await QueryAsync(request, nameof(SearchAsync), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var feature = $"distance(field,{property.StorageName})";
            if (hit.GetMatchFeature(feature) is not { } distance)
                throw Failure(nameof(SearchAsync), $"Rank profile '{binding.RankProfile}' must return match-feature '{feature}'.");
            double score;
            try
            {
                score = VespaVectorMetric.Score(property.DistanceFunction, distance);
            }
            catch (ArgumentException exception)
            {
                throw Failure(nameof(SearchAsync), $"Rank profile '{binding.RankProfile}' returned an invalid distance for '{property.Name}'.", exception);
            }
            if (distance < previousDistance)
                throw Failure(nameof(SearchAsync), $"Rank profile '{binding.RankProfile}' must order results by increasing distance for '{property.Name}', without unrelated reranking.");
            previousDistance = distance;
            if (options?.ScoreThreshold is { } threshold && !VespaVectorMetric.PassesThreshold(property.DistanceFunction, score, threshold))
                continue;
            yield return new VectorSearchResult<TRecord>(MapHit(hit, options?.IncludeVectors == true, nameof(SearchAsync)), score);
        }
    }

    /// <summary>Combines dense and lexical candidates using a separately deployed hybrid ranking profile.</summary>
    public async IAsyncEnumerable<VectorSearchResult<TRecord>> HybridSearchAsync<TInput>(TInput searchValue,
        ICollection<string> keywords, int top, HybridSearchOptions<TRecord>? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) where TInput : notnull
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(searchValue);
        ArgumentNullException.ThrowIfNull(keywords);
        ValidateWindow(top, options?.Skip ?? 0);
        ValidateThreshold(options?.ScoreThreshold);
        var property = SelectVector(options?.VectorProperty);
        var binding = Binding(property, hybrid: true);
        var translator = new VespaVectorFilter<TRecord>(_mapper);
        var textProperties = _mapper.Properties.Where(p => p.IsFullTextIndexed).ToArray();
        var textProperty = options?.AdditionalProperty is { } selector ? translator.ResolvePropertySelector(selector)
            : textProperties.Length == 1 ? textProperties[0]
            : throw new NotSupportedException("Select one full-text data property when the collection has zero or multiple full-text properties.");
        if (!textProperty.IsFullTextIndexed || textProperty.IsVector || textProperty.Type != typeof(string))
            throw new NotSupportedException("Hybrid lexical search requires a string data property marked IsFullTextIndexed.");
        var terms = keywords.ToArray();
        if (terms.Length == 0 || terms.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Hybrid search requires at least one nonempty keyword.", nameof(keywords));
        var filterPredicate = options?.Filter is { } filter ? translator.Translate(filter) : null;
        var window = top + (options?.Skip ?? 0);
        var vectorTarget = Math.Max(window, _options.HybridVectorTargetHits ?? window);
        var keywordTarget = Math.Max(window, _options.HybridKeywordTargetHits ?? window);
        var nearest = Nearest(property, binding.QueryTensorName!, vectorTarget);
        // JSON quoting keeps every keyword inside a YQL string literal; full-text
        // analysis is performed by the selected field, without accepting query syntax.
        var lexical = "({totalTargetHits:" + keywordTarget.ToString(CultureInfo.InvariantCulture) + "}weakAnd(" +
            string.Join(", ", terms.Select(term => $"{textProperty.StorageName} contains {JsonSerializer.Serialize(term)}")) + "))";
        var predicate = $"({nearest} or {lexical})";
        if (filterPredicate is not null)
            predicate = $"({filterPredicate}) and {predicate}";
        var vector = await QueryVectorAsync(searchValue, property, cancellationToken);
        var request = Request(predicate, top, options?.Skip ?? 0, options?.IncludeVectors == true);
        request.Ranking = new RankingConfig { Profile = binding.HybridRankProfile };
        request.Input = new() { [$"query({binding.QueryTensorName})"] = VespaVectorRecordMapper<TRecord>.Tensor(vector) };
        foreach (var hit in await QueryAsync(request, nameof(HybridSearchAsync), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(hit.Relevance))
                throw Failure(nameof(HybridSearchAsync), "The hybrid ranking profile must return a finite combined relevance score.");
            if (options?.ScoreThreshold is { } minimum && hit.Relevance < minimum)
                continue;
            yield return new VectorSearchResult<TRecord>(MapHit(hit, options?.IncludeVectors == true, nameof(HybridSearchAsync)), hit.Relevance);
        }
    }

    private VespaCollectionModel.MappedProperty SelectVector(LambdaExpression? selector)
    {
        if (selector is null)
            return _mapper.ResolveVector();
        var property = new VespaVectorFilter<TRecord>(_mapper).ResolvePropertySelector(selector);
        if (!property.IsVector)
            throw new NotSupportedException("Select a mapped vector property directly.");
        return property;
    }

    private VespaVectorFieldOptions Binding(VespaCollectionModel.MappedProperty property, bool hybrid)
    {
        _options.VectorFields.TryGetValue(property.Name, out var configured);
        if (_mapper.VectorProperties.Count > 1 && configured?.QueryTensorName is null)
            throw new NotSupportedException($"Vector property '{property.Name}' requires an explicit query tensor binding in VectorFields.");
        var binding = new VespaVectorFieldOptions
        {
            RankProfile = configured?.RankProfile ?? _options.RankProfile,
            QueryTensorName = configured?.QueryTensorName ?? _options.QueryTensorName,
            HybridRankProfile = configured?.HybridRankProfile ?? _options.HybridRankProfile
        };
        if (hybrid && binding.HybridRankProfile is null)
            throw new NotSupportedException("Hybrid search requires an explicitly configured HybridRankProfile that combines lexical and vector relevance.");
        if (!hybrid && (_mapper.VectorProperties.Count > 1 ? configured?.RankProfile : binding.RankProfile) is null)
            throw new NotSupportedException($"Vector property '{property.Name}' requires an explicit rank profile for vector search.");
        return binding;
    }

    private static IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator(VespaCollectionModel.MappedProperty property) =>
        property.EmbeddingGenerator switch
        {
            null => null,
            IEmbeddingGenerator<string, Embedding<float>> generator => generator,
            _ => throw new NotSupportedException($"Embedding generator for '{property.Name}' must support string inputs and Embedding<float> outputs.")
        };

    private async Task<float[]> QueryVectorAsync<TInput>(TInput searchValue, VespaCollectionModel.MappedProperty property, CancellationToken cancellationToken)
    {
        var vector = searchValue switch
        {
            ReadOnlyMemory<float> memory => memory.ToArray(),
            float[] array => (float[])array.Clone(),
            Embedding<float> embedding => embedding.Vector.ToArray(),
            string text when GetEmbeddingGenerator(property) is { } generator =>
                (await generator.GenerateVectorAsync(_options.QueryEmbeddingPrefix + text, cancellationToken: cancellationToken)).ToArray(),
            string => throw new NotSupportedException("Text queries require an IEmbeddingGenerator<string, Embedding<float>>."),
            _ => throw new NotSupportedException("Queries must be float[], ReadOnlyMemory<float>, Embedding<float>, or text with an embedding generator.")
        };
        VespaVectorRecordMapper<TRecord>.ValidateVector(property, vector);
        return vector;
    }

    private string Nearest(VespaCollectionModel.MappedProperty property, string queryTensor, int target, double? distanceThreshold = null)
    {
        var approximate = _options.Approximate && property.IndexKind != IndexKind.Flat;
        var exploration = approximate && _options.HnswExploreAdditionalHits is { } additional
            ? string.Create(CultureInfo.InvariantCulture, $",hnsw.exploreAdditionalHits:{additional}") : "";
        var threshold = distanceThreshold is { } maximum
            ? ",distanceThreshold:" + maximum.ToString("R", CultureInfo.InvariantCulture) : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"({{totalTargetHits:{target},approximate:{(approximate ? "true" : "false")}{exploration}{threshold}}}nearestNeighbor({property.StorageName}, {queryTensor}))");
    }

    private static void ValidateThreshold(double? threshold)
    {
        if (threshold is { } value && !double.IsFinite(value))
            throw new ArgumentException("The score threshold must be finite.", nameof(threshold));
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
        PresentationSummary = includeVectors ? _options.VectorSummaryClass : _options.SummaryClass,
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
        return MapFields(parts.UserSpecified, fields, includeVectors, operation,
            $"summary '{(includeVectors ? _options.VectorSummaryClass : _options.SummaryClass) ?? "(backend default)"}'");
    }

    private TRecord MapFields(string key, Dictionary<string, JsonElement> fields, bool includeVectors, string operation, string source)
    {
        try
        {
            return _mapper.FromFields(key, fields, includeVectors);
        }
        catch (Exception exception) when (exception is JsonException or VectorStoreException)
        {
            throw Failure(operation, $"Cannot read schema '{Name}' using {source}: {exception.Message}", exception);
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
}
