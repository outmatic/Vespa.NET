# Code-First Schema Generation

Vespa.NET generates Vespa `.sd` schemas and `services.xml` from C# model attributes. No hand-written schema files needed.

---

## Attributes

| Attribute | Target | Purpose |
|---|---|---|
| `[VespaDocument("type")]` | Class | Declares a Vespa document type; `Namespace`, `Inherits`, `Selection`, `Global`, `Mode` |
| `[VespaField(Name = "name")]` | Property | Maps to a Vespa field; drives schema and JSON serialization |
| `[VespaTensor("tensor<float>(x[N])")]` | Property | Tensor field with optional HNSW index, distance metric |
| `[VespaId]` | Property | Document ID property (excluded from JSON body) |
| `[VespaExtraFields]` | Property | Catch-all for unmapped fields (`Dictionary<string, JsonElement>?`) |
| `[VespaStruct]` | Class | Declares a Vespa struct type |
| `[VespaFieldSet("name", "f1", "f2")]` | Class | Declares a fieldset |
| `[VespaDocumentSummary("name")]` | Class | Declares a document summary |
| `[VespaRankProfile("name")]` | Class | Rank profile (first/second/global phase, features, diversity, functions) |
| `[VespaImportField("ref", "field", "alias")]` | Class | Import field from a referenced document |
| `[VespaOnnxModel("name", "file")]` | Class | ONNX model; `SourcePath` auto-bundles in ZIP |
| `[VespaConstant("name", "file", "type")]` | Class | Constant tensor; `SourcePath` auto-bundles |

---

## Document ID and `[VespaId]`

In Vespa, the document ID lives in the URL path — not the body. Mark the ID property with `[VespaId]` to exclude it from JSON serialization:

```csharp
[VespaDocument("product")]
public record Product
{
    [VespaId]
    public string Id { get; init; } = "";        // excluded from JSON, passed in the URL

    [VespaField(Name = "name")]
    public string Name { get; init; } = "";       // serialized as "name" in the body
}
```

> [!NOTE]
> Without `[VespaId]`, every property is serialized. If you forget it on your ID property, it will appear in the body — harmless but wasteful.

---

## Field Configuration

```csharp
// Indexing modes (combinable flags)
[VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]  // searchable + filterable + returned
[VespaField(Name = "category", IndexingMode = IndexingMode.AttributeSummary)]     // filterable + returned
[VespaField(Name = "body", IndexingMode = IndexingMode.SummaryIndex)]             // searchable + returned, no attribute

// Match, stemming, bolding
[VespaField(Name = "title", MatchMode = MatchMode.Text, Stemming = StemmingMode.Best, Bolding = true)]

// Fast-search attribute
[VespaField(Name = "category", FastSearch = true)]

// Reference field (for parent-child)
[VespaField(FieldType = VespaFieldType.Reference, ReferenceDocumentType = "campaign")]

// Tensor with full HNSW configuration
[VespaTensor("tensor<float>(x[384])",
    EnableIndex = true,
    DistanceMetric = DistanceMetric.Euclidean,
    MaxLinksPerNode = 32,
    NeighborsToExploreAtInsert = 500,
    IncludeInSummary = true)]
```

---

## Dynamic Fields with `[VespaExtraFields]`

For multi-tenant scenarios where some fields are defined at runtime, use `[VespaExtraFields]` to create a catch-all bag for unmapped fields (similar to MongoDB's `[BsonExtraElements]`):

```csharp
public record ProductFields
{
    // App-owned fields — strongly typed
    [VespaField(IndexingMode = IndexingMode.AttributeSummary)]
    public double Popularity { get; init; }

    [VespaField(IndexingMode = IndexingMode.AttributeSummary)]
    public int Frequency { get; init; }

    // Tenant-defined fields — dynamic, vary per customer
    [VespaExtraFields]
    public Dictionary<string, JsonElement>? TenantFields { get; init; }
}
```

**Behavior:**
- **Deserialization:** JSON fields not matched by a declared property are captured in the dictionary
- **Serialization:** Dictionary entries are emitted flat alongside declared properties (not nested)
- **Round-trip safe:** deserialize then serialize preserves all fields
- **Schema builder:** `[VespaExtraFields]` properties are ignored (no `.sd` field generated)

---

## Generate and Deploy

```csharp
// Deploy directly from type
await client.Admin.DeploySchemaAsync<Product>();

// With options
await client.Admin.DeploySchemaAsync<Product>(new ApplicationPackageOptions
{
    Redundancy = 2,
    NodeCount = 3
});

// Multi-document-type deployment
await client.Admin.DeploySchemaAsync([typeof(Product), typeof(Order)]);

// Generate schema string for inspection
var schema = VespaSchemaBuilder.GenerateSchema<Product>();
Console.WriteLine(schema);
```

---

## Application Package Options

```csharp
var opts = new ApplicationPackageOptions
{
    Redundancy = 2,
    NodeCount = 3,

    // Escape hatch: provide your own services.xml verbatim
    CustomServicesXml = File.ReadAllText("my-services.xml"),

    // Or inject XML fragments into the generated template
    ContainerOptions = new Dictionary<string, string>
    {
        ["handler"] = """<handler id="com.example.MyHandler" bundle="my-bundle"/>"""
    },
    ContentOptions = new Dictionary<string, string>
    {
        ["tuning"] = """
            <tuning>
                <dispatch>
                    <max-hits-per-partition>1000</max-hits-per-partition>
                </dispatch>
            </tuning>
        """
    },

    // Additional files in the ZIP
    AdditionalFiles = new Dictionary<string, string>
    {
        ["models/my_model.onnx"] = "/path/to/model.onnx"
    }
};
```

> [!NOTE]
> When `CustomServicesXml` is set, `ContainerOptions` and `ContentOptions` are ignored.
