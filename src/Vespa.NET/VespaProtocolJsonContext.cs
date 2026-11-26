using System.Text.Json;
using System.Text.Json.Serialization;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Tensors;

namespace Vespa;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    Converters = [typeof(VespaTensorConverter)])]
[JsonSerializable(typeof(DocumentWire))]
[JsonSerializable(typeof(VisitPageWire))]
[JsonSerializable(typeof(SelectionResponseBody))]
[JsonSerializable(typeof(FieldUpdateWire))]
[JsonSerializable(typeof(SearchResponseWire))]
[JsonSerializable(typeof(VespaSearchRequest))]
[JsonSerializable(typeof(VespaError))]
[JsonSerializable(typeof(List<VespaError>))]
[JsonSerializable(typeof(VespaStateConfig))]
[JsonSerializable(typeof(VespaMetrics))]
[JsonSerializable(typeof(VespaApplicationStatus))]
[JsonSerializable(typeof(Dictionary<string, object?>), TypeInfoPropertyName = "WriteFields")]
[JsonSerializable(typeof(Dictionary<string, JsonElement>), TypeInfoPropertyName = "ReadFields")]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(VespaTensor))]
[JsonSerializable(typeof(float))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(float[]))]
[JsonSerializable(typeof(bool[]))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(long[]))]
internal sealed partial class VespaProtocolJsonContext : JsonSerializerContext;

internal sealed class DocumentWire
{
    [JsonPropertyName("id")]
    public string FullId { get; set; } = string.Empty;

    [JsonPropertyName("pathId")]
    public string? PathId { get; set; }

    public JsonElement Fields { get; set; }

    internal VespaDocument<T> ToDocument<T>(ModelJson<T> model) where T : class => new()
    {
        FullId = FullId,
        PathId = PathId,
        Fields = model.ReadFields(Fields, FullId)
    };
}

internal sealed record VisitPageWire(
    List<DocumentWire>? Documents,
    string? Continuation,
    [property: JsonPropertyName("documentCount")] int DocumentCount,
    [property: JsonPropertyName("pathId")] string? PathId);

internal sealed record FieldUpdateWire(Dictionary<string, FieldOperation> Fields);

internal sealed class SearchResponseWire
{
    public SearchRootWire Root { get; set; } = new();
    public TimingInfo? Timing { get; set; }
    public TraceInfo? Trace { get; set; }

    internal VespaSearchResponse<T> ToResponse<T>(ModelJson<T> model) where T : class
    {
        var children = new List<SearchHit<T>>(Root.Children.Count);
        Func<JsonElement, string, T?> readFields = model.ReadFields;
        foreach (var hit in Root.Children)
            children.Add(SearchHitConverter<T>.Read(hit, VespaProtocolJsonContext.Default.Options, readFields));

        return new VespaSearchResponse<T>
        {
            Root = new SearchRoot<T>
            {
                Id = Root.Id,
                Relevance = Root.Relevance,
                Fields = Root.Fields,
                Coverage = Root.Coverage,
                Children = children,
                Errors = Root.Errors
            },
            Timing = Timing,
            Trace = Trace
        };
    }
}

internal sealed class SearchRootWire
{
    public string Id { get; set; } = string.Empty;
    public double Relevance { get; set; }
    public SearchFields? Fields { get; set; }
    public Coverage? Coverage { get; set; }
    public List<JsonElement> Children { get; set; } = [];
    public List<VespaError>? Errors { get; set; }
}
