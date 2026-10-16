using System.Globalization;
using Vespa.Models;

namespace Vespa.Documents;

public sealed partial class DocumentOperations
{
    public Task<VespaDocument<T>?> GetByGroupAsync<T>(
        string group,
        string localId,
        string documentType,
        string? @namespace = null,
        string? fieldSet = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => GetByAddressingAsync<T>("group", group, localId, documentType, @namespace, fieldSet, requestOptions, cancellationToken);

    public Task<VespaDocument<T>?> GetByNumberAsync<T>(
        long number,
        string localId,
        string documentType,
        string? @namespace = null,
        string? fieldSet = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => GetByAddressingAsync<T>("number", number.ToString(CultureInfo.InvariantCulture), localId, documentType, @namespace, fieldSet, requestOptions, cancellationToken);

    public Task<VespaResponse> PutByGroupAsync<T>(
        string group,
        string localId,
        T document,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        bool create = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => PutByAddressingAsync("group", group, localId, document, documentType, @namespace, condition, create, requestOptions, cancellationToken);

    public Task<VespaResponse> PutByNumberAsync<T>(
        long number,
        string localId,
        T document,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        bool create = false,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default) where T : class
        => PutByAddressingAsync("number", number.ToString(CultureInfo.InvariantCulture), localId, document, documentType, @namespace, condition, create, requestOptions, cancellationToken);

    public Task<VespaResponse> UpdateFieldsByGroupAsync(
        string group,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        bool createIfMissing = false,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => UpdateFieldsByAddressingAsync("group", group, localId, fieldOperations, documentType, @namespace, createIfMissing, condition, requestOptions, cancellationToken);

    public Task<VespaResponse> UpdateFieldsByNumberAsync(
        long number,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace = null,
        bool createIfMissing = false,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => UpdateFieldsByAddressingAsync("number", number.ToString(CultureInfo.InvariantCulture), localId, fieldOperations, documentType, @namespace, createIfMissing, condition, requestOptions, cancellationToken);

    public Task<VespaResponse> DeleteByGroupAsync(
        string group,
        string localId,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => DeleteByAddressingAsync("group", group, localId, documentType, @namespace, condition, requestOptions, cancellationToken);

    public Task<VespaResponse> DeleteByNumberAsync(
        long number,
        string localId,
        string documentType,
        string? @namespace = null,
        string? condition = null,
        DocumentRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
        => DeleteByAddressingAsync("number", number.ToString(CultureInfo.InvariantCulture), localId, documentType, @namespace, condition, requestOptions, cancellationToken);

    private async Task<VespaDocument<T>?> GetByAddressingAsync<T>(
        string scheme,
        string bucket,
        string localId,
        string documentType,
        string? @namespace,
        string? fieldSet,
        DocumentRequestOptions? requestOptions,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        var (ns, url) = BuildAddressingUrl(scheme, bucket, localId, documentType, @namespace, requestOptions,
            (QueryParamFieldSet, fieldSet),
            (QueryParamCluster, requestOptions?.Cluster));

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentGet, documentType, ns);
        return await GetDocumentOrNullAsync<T>(url, null, cancellationToken);
    }

    private async Task<VespaResponse> PutByAddressingAsync<T>(
        string scheme,
        string bucket,
        string localId,
        T document,
        string documentType,
        string? @namespace,
        string? condition,
        bool create,
        DocumentRequestOptions? requestOptions,
        CancellationToken cancellationToken) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(document);

        var (ns, url) = BuildAddressingUrl(scheme, bucket, localId, documentType, @namespace, requestOptions,
            (QueryParamCondition, condition),
            (QueryParamCreate, create ? "true" : null));

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentPut, documentType, ns);

        return await SendPutAsync(url, document, cancellationToken);
    }

    private async Task<VespaResponse> UpdateFieldsByAddressingAsync(
        string scheme,
        string bucket,
        string localId,
        Dictionary<string, FieldOperation> fieldOperations,
        string documentType,
        string? @namespace,
        bool createIfMissing,
        string? condition,
        DocumentRequestOptions? requestOptions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(fieldOperations);

        var (ns, url) = BuildAddressingUrl(scheme, bucket, localId, documentType, @namespace, requestOptions,
            (QueryParamCreate, createIfMissing ? "true" : null),
            (QueryParamCondition, condition));

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentUpdate, documentType, ns);

        return await SendUpdateAsync(url, fieldOperations, cancellationToken);
    }

    private async Task<VespaResponse> DeleteByAddressingAsync(
        string scheme,
        string bucket,
        string localId,
        string documentType,
        string? @namespace,
        string? condition,
        DocumentRequestOptions? requestOptions,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localId);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);

        var (ns, url) = BuildAddressingUrl(scheme, bucket, localId, documentType, @namespace, requestOptions,
            (QueryParamCondition, condition));

        using var activity = StartDocumentActivity(VespaActivitySource.DocumentDelete, documentType, ns);

        return await SendDeleteAsync(url, cancellationToken);
    }

    private (string Namespace, string Url) BuildAddressingUrl(
        string scheme,
        string bucket,
        string localId,
        string documentType,
        string? @namespace,
        DocumentRequestOptions? requestOptions,
        params (string Key, string? Value)[] extraParams)
    {
        var ns = ResolveNamespace(@namespace);
        var url = BuildUrl(
            $"{DocTypePrefix(documentType, ns)}/{scheme}/{Uri.EscapeDataString(bucket)}/{Uri.EscapeDataString(localId)}",
            requestOptions,
            extraParams);
        return (ns, url);
    }
}
