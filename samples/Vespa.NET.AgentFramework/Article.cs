using Microsoft.Extensions.VectorData;

namespace Vespa.AgentFrameworkSample;

public sealed class Article
{
    [VectorStoreKey]
    public string Id { get; set; } = "";

    [VectorStoreData(StorageName = "title")]
    public string Title { get; set; } = "";

    [VectorStoreData(StorageName = "category", IsIndexed = true)]
    public string Category { get; set; } = "docs";

    [VectorStoreData(StorageName = "priority", IsIndexed = true)]
    public int Priority { get; set; } = 1;

    [VectorStoreData(StorageName = "published", IsIndexed = true)]
    public bool Published { get; set; } = true;

    [VectorStoreVector(768, StorageName = "embedding", DistanceFunction = DistanceFunction.CosineSimilarity)]
    public ReadOnlyMemory<float> Embedding { get; set; }
}
