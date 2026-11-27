# Trimming and Native AOT warning inventory

Use registered JSON and schema metadata for trimmed and Native AOT applications. The configuration guide describes the tested paths and how to register models, projections, and schema types. [Trimming and Native AOT](configuration.md#trimming-and-native-aot)

The source projects enable both analyzers. This inventory covers 26 suppressions across 15 members. Rows with `IL2026` and `IL3050` cover two warnings on the same member. The last column describes the change that would remove the need for the suppression. [Analyzer settings](../src/Directory.Build.props#L9-L12)

## Vespa.NET

| Source | Diagnostic | Why it is suppressed | What would remove it |
| --- | --- | --- | --- |
| [DocumentFieldSelector.cs:53](../src/Vespa.NET/Documents/DocumentFieldSelector.cs#L53) · `EffectiveProperty` | `IL2075` | The model annotation preserves inherited interface members. The analyzer loses that guarantee for individual types returned by `GetInterfaces()`. | Have the analyzer track the preserved interface types, or resolve inherited properties from generated field metadata. |
| [DocumentOperations.Http.cs:34, 36](../src/Vespa.NET/Documents/DocumentOperations.Http.cs#L34-L37) · `SendPutAsync` | `IL2026`, `IL3050` | PUT falls back to reflection JSON when reflection is enabled and no models are registered. With reflection disabled, the write requires the registered model's `JsonTypeInfo`. | Use registered model metadata for every PUT. |
| [DocumentOperations.Http.cs:55, 57](../src/Vespa.NET/Documents/DocumentOperations.Http.cs#L55-L58) · `CreateUpdateContent` | `IL2026`, `IL3050` | The reflection fallback serializes the update envelope via default options. The generated path serializes `FieldUpdateWire` with protocol metadata. | Use the generated envelope for every update and require metadata or JSON values for application-defined values. |
| [DocumentOperations.Visiting.cs:270, 272](../src/Vespa.NET/Documents/DocumentOperations.Visiting.cs#L270-L273) · `ParseJsonlDocument` | `IL2026`, `IL3050` | The JSONL reader uses reflection for fields only when reflection is enabled and no model is registered. It otherwise reads them through the registered model. | Read JSONL fields through registered metadata on every visit. |
| [GroupingResponseParser.cs:74, 76](../src/Vespa.NET/Search/GroupingResponseParser.cs#L74-L77) · `CreateHitReader` | `IL2026`, `IL3050` | Grouping hits fall back to reflection JSON when reflection is enabled and no model is registered. The alternate reader uses generated protocol metadata and registered fields metadata. | Always use the metadata-backed grouping hit reader. |
| [SearchOperations.cs:192, 194](../src/Vespa.NET/Search/SearchOperations.cs#L192-L195) · `CreateSearchContent` | `IL2026`, `IL3050` | The request can use the default reflection serializer when reflection is enabled. The other branch serializes with the generated search request contract. | Use the generated search request contract for every request and require metadata or JSON values for application-defined input. |
| [VespaIdInjector.cs:24, 26](../src/Vespa.NET/VespaIdInjector.cs#L24-L27) · `DeserializeAndInjectAsync` | `IL2026`, `IL3050` | Document reads call this fallback only when reflection JSON is enabled and no model is registered. The registered path reads the fields with `JsonTypeInfo` and can set the ID through a callback. | Use registered metadata for all typed document reads. |
| [VespaIdInjector.cs:41, 43](../src/Vespa.NET/VespaIdInjector.cs#L41-L44) · `DeserializeSearchAndInjectAsync` | `IL2026`, `IL3050` | Search uses this fallback only when reflection JSON is enabled and no model is registered. The registered path reads the envelope and fields with metadata and can set the ID through a callback. | Use the metadata-backed response reader for all typed searches. |
| [VespaIdInjector.cs:59, 61](../src/Vespa.NET/VespaIdInjector.cs#L59-L62) · `DeserializeVisitAndInjectAsync` | `IL2026`, `IL3050` | Paged visits use this fallback only when reflection JSON is enabled and no model is registered. The registered path reads each document with metadata and can set the ID through a callback. | Use the metadata-backed response reader for all typed paged visits. |
| [VespaIdInjector.cs:95](../src/Vespa.NET/VespaIdInjector.cs#L95) · `GetOrCreateSetter` | `IL2070` | The fallback inspects properties for `[VespaId]` on a runtime type. Reflection-disabled reads use the registered model's optional ID callback and skip this scan. | Supply ID assignment through registered callbacks or generated setters for all reads. |
| [VespaJsonModels.cs:109](../src/Vespa.NET/VespaJsonModels.cs#L109) · `GetCollectionElementType` | `IL2075` | On .NET 8, the nested tensor guard inspects collection interfaces already used by the registered JSON converter. The newer target reads `JsonTypeInfo.ElementType` directly. | Make equivalent element type metadata available to the .NET 8 target. |
| [VespaJsonOptions.cs:13, 15](../src/Vespa.NET/VespaJsonOptions.cs#L13-L16) · `CreateDefault` | `IL2026`, `IL3050` | The default options construct a reflection resolver with Vespa's field naming and tensor rules. Reflection-disabled operations use generated metadata. | Move all callers to generated metadata and remove the default reflection resolver. |

## Vespa.NET.Models

| Source | Diagnostic | Why it is suppressed | What would remove it |
| --- | --- | --- | --- |
| [GroupingAggregation.cs:42, 44](../src/Vespa.NET.Models/GroupingAggregation.cs#L42-L45) · `Deserialize` | `IL2026`, `IL3050` | Aggregation results use reflection JSON when enabled. With reflection disabled, the caller must provide options that resolve the result type to generated metadata. | Require metadata for typed aggregation results in either mode. |
| [SearchHitConverter.cs:17](../src/Vespa.NET.Models/SearchHitConverter.cs#L17) · `CreateConverter` | `IL3050` | The factory checks `RuntimeFeature.IsDynamicCodeSupported` before closing the generic converter. Native AOT client calls read hits with registered fields metadata and bypass the factory. | Use closed converters registered for each fields type or route direct response reads through generated metadata without the runtime factory. |

## Vespa.NET.AgentFramework

| Source | Diagnostic | Why it is suppressed | What would remove it |
| --- | --- | --- | --- |
| [VespaChatHistoryProvider.cs:81, 83](../src/Vespa.NET.AgentFramework/VespaChatHistoryProvider.cs#L81-L84) · `CreateHistoryMetadata` | `IL2026`, `IL3050` | History uses the default serializer for application-defined message values when reflection is enabled. Otherwise, history uses the generated context and custom tool results can be supplied as `JsonElement`. | Use generated history metadata in either mode and accept typed metadata or `JsonElement` for custom message values. |

On .NET 8, the Models package embeds a linker substitution for the JSON reflection switch. When the switch is disabled, the linker can replace the library's reflection check with `false`. [Linker substitution](../src/Vespa.NET.Models/ILLink.Substitutions.xml)
