using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vespa.Models.Tensors;

namespace Vespa;

internal static class VespaJsonOptions
{
    internal static readonly JsonSerializerOptions Default = CreateDefault();

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The default options are used only by the reflection JSON fallback. The registered path uses generated contexts.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "The reflection-disabled client never initializes these fallback options.")]
    private static JsonSerializerOptions CreateDefault() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false,
        Converters = { new VespaTensorConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { VespaFieldJsonModifier.UseVespaFieldNames }
        }
    };
}
