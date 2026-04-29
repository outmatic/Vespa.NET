using System.Text.Json.Serialization.Metadata;
using Vespa.Models.Attributes;

namespace Vespa;

/// <summary>
/// <see cref="IJsonTypeInfoResolver"/> modifier that bridges Vespa attributes
/// to <c>System.Text.Json</c> serialization:
/// <list type="bullet">
///   <item><see cref="VespaFieldAttribute.Name"/> → JSON property name</item>
///   <item><see cref="VespaIdAttribute"/> → excluded from serialization (document ID is passed in the URL, not the body)</item>
///   <item><see cref="VespaExtraFieldsAttribute"/> → wired as <c>System.Text.Json</c> extension data (catch-all for unmapped fields)</item>
/// </list>
/// </summary>
internal static class VespaFieldJsonModifier
{
    internal static void UseVespaFieldNames(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        foreach (var prop in typeInfo.Properties)
        {
            if (prop.AttributeProvider is null)
                continue;

            var hasVespaId = prop.AttributeProvider
                .GetCustomAttributes(typeof(VespaIdAttribute), inherit: false)
                .Length > 0;

            // [VespaId] → exclude from serialization
            if (hasVespaId)
            {
                prop.ShouldSerialize = static (_, _) => false;
                continue;
            }

            // [VespaExtraFields] → extension data (catch-all for unmapped fields)
            var hasExtraFields = prop.AttributeProvider
                .GetCustomAttributes(typeof(VespaExtraFieldsAttribute), inherit: false)
                .Length > 0;

            if (hasExtraFields)
            {
                prop.IsExtensionData = true;
                continue;
            }

            // [VespaField(Name = "...")] → use as JSON property name
            var fieldAttr = prop.AttributeProvider
                .GetCustomAttributes(typeof(VespaFieldAttribute), inherit: false)
                .OfType<VespaFieldAttribute>()
                .FirstOrDefault();

            if (fieldAttr?.Name is not null)
                prop.Name = fieldAttr.Name;
        }
    }
}
