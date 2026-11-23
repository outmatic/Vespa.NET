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
| `[VespaRankProfile("name")]` | Class | Rank profile (first/second/global phase, features, diversity) |
| `[VespaRankFunction("sig", "expr")]` | Class | Named rank-profile function; `Profile` targets a specific profile |
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

// Predicate field — Vespa mandates an arity
[VespaField(FieldType = VespaFieldType.Predicate, PredicateArity = 8,
    PredicateLowerBound = 0, PredicateUpperBound = 1000)]
```

### Type mapping

C# property types are inferred as follows:

| C# | Vespa |
|---|---|
| `string`, `Guid`, `char` | `string` |
| `int`, `short`, `ushort` | `int` |
| `long`, `uint` | `long` |
| `float` / `double`, `decimal` | `float` / `double` |
| `bool`, `sbyte` | `bool`, `byte` |
| `byte[]` | `raw` (base64, matching JSON serialization) |
| `T[]`, `List<T>`, `IList<T>`, `IEnumerable<T>`, `IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `ICollection<T>`, `HashSet<T>`, `ISet<T>`, `IReadOnlySet<T>` | `array<T>` |
| `Dictionary<string, int>` | `weightedset<string>` |
| other `Dictionary<K,V>` / `IDictionary` / `IReadOnlyDictionary` | `map<K,V>` |
| `[VespaStruct]` type (also nested) | struct |

Unmappable types **throw at generation time** instead of silently producing a
schema that fails at feed time: C# `byte` (0–255 does not fit Vespa's signed
byte — use `sbyte` or `short`), `ulong` (no unsigned 64-bit), enums (serialized
as numbers by default — use `int`/`long`, or a string-serialized property with
`FieldType = VespaFieldType.String`), and anything else unknown (e.g. `DateTime`
— store epoch seconds in a `long` or an ISO string).

### Struct, array-of-struct and map fields

Vespa does not allow `attribute`/`index` on the outer field of structs and maps.
The `IndexingMode` flags are translated instead:

- `Summary` → `indexing: summary` (whole field in the document summary)
- `Attribute` → `struct-field <member> { indexing: attribute }` for every
  *primitive-typed* struct member; for maps, `key`/`value` (or `value.<member>`
  when the map value is a struct). Vespa supports these struct-field attributes
  on **array-of-struct and map fields only** — on a *single* struct field
  `Attribute` **throws** (use `IndexingMode.Summary`)
- `Index` → **throws** (only supported in streaming search)

```csharp
[VespaField]                                      // default AttributeSummary:
public Dictionary<string, string>? Labels { get; init; }
// → indexing: summary
//   struct-field key { indexing: attribute }
//   struct-field value { indexing: attribute }

[VespaField(IndexingMode = IndexingMode.Summary)] // single struct: summary is the
public Address? HomeAddress { get; init; }        // only supported mode
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

`client.Admin` deploys to a self-hosted config server. To deploy a generated package
to Vespa Cloud, use the Cloud CLI or CI/CD pipeline, then connect the client to
the deployed application's endpoint. See [deployment configuration](agent-framework.md#configure-application-deployment-from-c).

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

    // Multi-node (NodeCount > 1) packages get a generated hosts.xml with
    // aliases node1..nodeN; map them to real hostnames here (count must match).
    // Omit to use the aliases as hostnames (Docker-compose-style networks).
    HostNames = ["vespa0.example.com", "vespa1.example.com", "vespa2.example.com"],

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

---

## Bundled Files

Set `SourcePath` on `[VespaOnnxModel]` or `[VespaConstant]` to copy a local file into the application package. `SourcePath` is an absolute filesystem path or a path relative to the process's current working directory. The attribute's `File` argument is the destination inside the ZIP, relative to the package root, such as `models/scorer.onnx` or `constants/weights.json`.

ZIP entry names are compared exactly and case-sensitively. Document types are processed in the supplied order, with ONNX resources before constants within each type; for an identical entry name, the first source wins. `AdditionalFiles` cannot replace that entry. Use distinct destination paths for files with different contents.

Bundled resources cannot share a destination with `services.xml`, a generated `hosts.xml` or any generated `schemas/<type>.sd`. Such a collision throws `InvalidOperationException`, including when the conflicting schema belongs to a later type in the sequence. Use `CustomServicesXml` to provide your own service configuration.

Package generation finishes each ZIP entry before copying the next resource and leaves the caller's output stream open. A missing source file throws `FileNotFoundException` with its source path. This bundles files for deployment; their contents must still satisfy the corresponding Vespa model or tensor format.

---

## Document Expiry (Selection)

A document selection tells Vespa which documents to keep. The default feed route evaluates the selection, and the SDK enables garbage collection in the generated `services.xml`. A PUT that fails the selection can return HTTP 200 even though the cluster did not store the document. Garbage collection can remove stored documents that fail the selection in the background. [Document routing](https://docs.vespa.ai/en/writing/document-routing.html#document-selection) · [Content cluster configuration](https://docs.vespa.ai/en/reference/applications/services/content.html#documents)

Use `[VespaDocument(Selection = "…")]` for a policy fixed in the model. This example keeps music documents with a Unix timestamp newer than a rolling one-day cutoff. The generator writes the selection into `services.xml`:

```csharp
[VespaDocument("music", Selection = "music.timestamp > now() - 86400")]
```

```xml
<documents garbage-collection="true">
  <document type="music" mode="index" selection="music.timestamp &gt; now() - 86400"/>
</documents>
```

In 4.0 you can also configure a policy when building the package. For a model named `product` with a `long Timestamp` property in Unix seconds, the following example sets a fixed cutoff. `WithDocumentSelection` reads the captured value immediately and returns a new options record. Pass that record to the existing package generator or deployment method.

```csharp
using Vespa.Models.Schema;

long cutoff = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
var options = new ApplicationPackageOptions()
    .WithDocumentSelection<Product>(p => p.Timestamp > cutoff);

VespaSchemaBuilder.GenerateApplicationPackage<Product>(output, options);
```

For a rolling one-day cutoff, let Vespa evaluate `now()` in a native selector. Set `DocumentSelections` on a separate options instance:

```csharp
var rollingOptions = new ApplicationPackageOptions
{
    DocumentSelections = new Dictionary<string, string>
    {
        ["product"] = "product.timestamp > now() - 86400"
    }
};
```

The examples assume stored documents have a timestamp. An ordering comparison against a missing timestamp evaluates to `invalid`, so garbage collection keeps those documents. To make documents with a missing timestamp eligible for removal, use an explicit presence check, for example `(product.timestamp != null) and (product.timestamp > now() - 86400)`. [Comparisons with missing fields](https://docs.vespa.ai/en/reference/writing/document-selector-language.html#comparisons-with-missing-fields-null-values)

The options snapshot the dictionary when assigned and compare document type names case-sensitively. Each configured type must occur in the package and may have only one selection. Adding a typed selection for an already configured type throws. Package generation rejects a selection for a missing type or a type that also declares an attribute selection, even an empty one. Configured selections also conflict with `CustomServicesXml`. These conflicts are detected before the ZIP is written.

Typed policies support scalar properties declared with `[VespaField]`, including strings, numbers, booleans and scalar null checks. The CLR property and its declared Vespa field must share the same category: string, boolean or numeric. The renderer allows routing to proceed when an operation provides only a document ID and omits the fields in the selection. Typed policies reject collection null checks and ordering comparisons between two fields. Decimal fields use Vespa's double precision. When both operands are native integers, at least one must be a native `byte` or `int` field or a constant or captured value strictly between −2⁵³ and 2⁵³. This keeps comparisons consistent between routing and garbage collection.

Typed policies accept printable ASCII string literals, plus tab, newline, carriage return and form feed. They reject a trailing backslash, other characters and the minimum signed 64-bit integer literal. The Unicode restriction covers a Java-to-C++ garbage collection parser failure reproduced on Vespa 8.703.17. The other guards protect values that the routing or garbage collection parser may interpret incorrectly. They apply to selection literals; document contents may still contain Unicode. Native selectors in `DocumentSelections` are forwarded as supplied and must work in both routing and garbage collection.
