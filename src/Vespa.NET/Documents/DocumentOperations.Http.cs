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
        var payload = new { fields = document };
        using var response = await _httpClient.PostAsJsonAsync(url, payload, VespaJsonOptions.Default, cancellationToken);
        return await ParseResponseAsync(response, cancellationToken);
    }

    private async Task<VespaResponse> SendUpdateAsync(string url, Dictionary<string, FieldOperation> fieldOperations, CancellationToken cancellationToken)
    {
        var payload = new { fields = fieldOperations };
        using var response = await _httpClient.PutAsJsonAsync(url, payload, VespaJsonOptions.Default, cancellationToken);
        return await ParseResponseAsync(response, cancellationToken);
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
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            if (documentId is not null && logger != null)
                LogDocumentNotFound(logger, documentId);
            return null;
        }

        if (!response.IsSuccessStatusCode)
            throw await VespaErrorResponse.CreateAsync(response, cancellationToken);

        return await VespaIdInjector.DeserializeAndInjectAsync<T>(response.Content, cancellationToken);
    }
}
