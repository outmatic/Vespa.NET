using System.Net;
using System.Net.Http.Json;
using Vespa.Models;

namespace Vespa.Documents;

public sealed partial class DocumentOperations
{
    /// <summary>
    /// <c>X-Vespa-Ignored-Fields</c> is a boolean flag header — Vespa sets it to
    /// <c>true</c> when the operation contained ignored fields; it never lists them.
    /// </summary>
    private static bool HasIgnoredFields(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Vespa-Ignored-Fields", out var headerValues) &&
        headerValues.Any(v => string.Equals(v.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    private static async Task<VespaResponse> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        if (!response.IsSuccessStatusCode)
            throw await VespaErrorResponse.CreateAsync(response, cancellationToken);

        return new VespaResponse
        {
            IsSuccess = true,
            StatusCode = (int)response.StatusCode,
            Message = "Operation completed successfully",
            HadIgnoredFields = HasIgnoredFields(response)
        };
    }

    private async Task<VespaResponse> SendPutAsync<T>(string url, T document, CancellationToken cancellationToken) where T : class
    {
        var model = VespaJsonModels.Resolve<T>(_options.JsonModels, "document writes");
        using var content = VespaJsonReflection.IsEnabled && model is null
            ? (HttpContent)JsonContent.Create(new { fields = document }, options: VespaJsonOptions.Default)
            : new DocumentJsonContent<T>(document, model!.TypeInfo);
        using var response = await _httpClient.PostAsync(url, content, cancellationToken);
        return await ParseResponseAsync(response, cancellationToken);
    }

    private async Task<VespaResponse> SendUpdateAsync(string url, Dictionary<string, FieldOperation> fieldOperations, CancellationToken cancellationToken)
    {
        using var content = CreateUpdateContent(fieldOperations);
        using var response = await _httpClient.PutAsync(url, content, cancellationToken);
        return await ParseResponseAsync(response, cancellationToken);
    }

    private JsonContent CreateUpdateContent(Dictionary<string, FieldOperation> fieldOperations)
    {
        if (VespaJsonReflection.IsEnabled && _options.JsonModels is null)
            return JsonContent.Create(new { fields = fieldOperations }, options: VespaJsonOptions.Default);

        return JsonContent.Create(new FieldUpdateWire(fieldOperations), VespaProtocolJsonContext.Default.FieldUpdateWire);
    }

    private async Task<VespaResponse> SendDeleteAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.DeleteAsync(url, cancellationToken);
        return await ParseResponseAsync(response, cancellationToken);
    }

    private async Task<VespaDocument<T>?> GetDocumentOrNullAsync<T>(
        string url,
        string? documentId,
        CancellationToken cancellationToken) where T : class
    {
        var model = VespaJsonModels.Resolve<T>(_options.JsonModels, "document reads");
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (documentId is not null && logger != null)
                LogDocumentNotFound(logger, documentId);
            return null;
        }

        if (!response.IsSuccessStatusCode)
            throw await VespaErrorResponse.CreateAsync(response, cancellationToken);

        if (VespaJsonReflection.IsEnabled && model is null)
            return await VespaIdInjector.DeserializeAndInjectAsync<T>(response.Content, cancellationToken);

        var wire = await response.Content.ReadFromJsonAsync(VespaProtocolJsonContext.Default.DocumentWire, cancellationToken);
        return wire?.ToDocument(model!);
    }
}
