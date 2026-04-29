using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Vespa.Models.Tensors;

namespace Vespa.Samples;

[VespaDocument("transaction", Namespace = "default")]
public record Transaction
{
    [VespaId]
    public string Id { get; init; } = "";

    [VespaField(Name = "merchant_id")]
    public string MerchantId { get; init; } = "";

    [VespaField(Name = "amount")]
    public decimal Amount { get; init; }

    [VespaField(Name = "category", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Category { get; init; } = "";

    [VespaTensor("tensor<float>(x[8])", EnableIndex = true, DistanceMetric = DistanceMetric.Euclidean)]
    public VespaTensor Embedding { get; init; } = null!;
}
