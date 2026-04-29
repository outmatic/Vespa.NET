using System.Collections.Concurrent;

namespace Vespa.Tests.Helpers;

/// <summary>
/// Mock HttpMessageHandler for testing HTTP requests
/// Allows queuing responses and capturing sent requests
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<HttpResponseMessage> _responses = new();
    private readonly ConcurrentQueue<HttpRequestMessage> _requests = new();

    /// <summary>
    /// List of all requests that were sent
    /// </summary>
    public IReadOnlyList<HttpRequestMessage> Requests => [.. _requests];

    /// <summary>
    /// Queue a response to be returned for the next request
    /// </summary>
    public void QueueResponse(HttpResponseMessage response) =>
        _responses.Enqueue(response);

    /// <summary>
    /// Queue multiple responses
    /// </summary>
    public void QueueResponses(params HttpResponseMessage[] responses)
    {
        foreach (var response in responses)
            _responses.Enqueue(response);
    }

    /// <summary>
    /// Clear all queued responses and captured requests
    /// </summary>
    public void Reset()
    {
        _responses.Clear();
        _requests.Clear();
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Clone the request to capture it (requests can only be sent once)
        var capturedRequest = await CloneRequestAsync(request);
        _requests.Enqueue(capturedRequest);

        if (!_responses.TryDequeue(out var response))
            throw new InvalidOperationException(
                "No response queued. Use QueueResponse() to add expected responses.");

        return response;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        // Clone headers
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        // Clone content if present
        if (request.Content != null)
        {
            var contentBytes = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(contentBytes);

            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
