using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Vespa.Documents;

internal sealed class DocumentJsonContent<T> : HttpContent where T : class
{
    private static readonly ReadOnlyMemory<byte> FieldsPrefix = "{\"fields\":"u8.ToArray();
    private static readonly ReadOnlyMemory<byte> ObjectEnd = "}"u8.ToArray();
    private readonly T _document;
    private readonly JsonTypeInfo<T> _typeInfo;

    internal DocumentJsonContent(T document, JsonTypeInfo<T> typeInfo)
    {
        _document = document;
        _typeInfo = typeInfo;
        Headers.ContentType = new MediaTypeHeaderValue("application/json", "utf-8");
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(
        Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(FieldsPrefix, cancellationToken);
        await JsonSerializer.SerializeAsync(stream, _document, _typeInfo, cancellationToken);
        await stream.WriteAsync(ObjectEnd, cancellationToken);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
