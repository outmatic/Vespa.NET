namespace Vespa.VectorData;

/// <summary>Registers JSON metadata used by the VectorData provider.</summary>
public static class VespaVectorDataJsonExtensions
{
    /// <summary>Includes the field models for VectorData document and search requests.</summary>
    public static VespaJsonModels WithVectorData(this VespaJsonModels models)
    {
        ArgumentNullException.ThrowIfNull(models);
        return models.WithFieldMaps();
    }
}
