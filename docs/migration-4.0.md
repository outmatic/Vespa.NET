# Migrating to Vespa.NET 4.0

This guide covers the planned 4.0 API changes. The release date and the rest of the major's scope are still being decided. Model-aware calls require a `[VespaDocument]` attribute; calls with an explicit document type can use an undecorated model.

## Vespa server compatibility

The server baseline for the core client and VectorData is Vespa 8.640.27. The nearest-neighbor helper uses `totalTargetHits` with `nearestNeighbor`, and VectorData hybrid search also uses it with `weakAnd`. The integration checks ran on 8.640.27 and 8.788.13.

Individual server features can require a newer release. For example, `totalTargetHits` with `wand` requires 8.664.22, and the ANN time budget requires 8.684.9. [YQL builder](yql-builder.md#clause-level-predicates) · [Search](search.md)

Use `MinimumRequiredVespaVersion` to have Vespa check the requirement when deploying a generated package. `GetVersionAsync` reports the version of the service that answers. During a rolling upgrade, choose feature requirements based on the oldest service that can handle the operation. [Package configuration](schema.md#minimum-server-version) · [Vespa State API](https://docs.vespa.ai/en/reference/api/state-v1.html)

## Conditions

Model-aware writes use `condition:` for a lambda and `rawCondition:` for a native Vespa selector string:

```csharp
// 3.x
await client.Documents.PutAsync(product.Id, product,
    condition: "product.price > 0");

// 4.0: keep the native selector
await client.Documents.PutAsync(product.Id, product,
    rawCondition: "product.price > 0");

// 4.0: use a typed predicate
await client.Documents.PutAsync(product.Id, product,
    condition: p => p.Price > 0);
```

This also applies to model-aware deletes, dictionary updates, builder updates, and their group and number variants. Supply one condition argument at a time. Two non-null conditions throw `ArgumentException` before the update builder runs or any HTTP request is sent.

Typed predicates compare enum properties through their supported numeric backing value; use `rawCondition:` when a JSON converter on the property or enum type may change the wire representation. Use `rawCondition:` for value comparisons on `DateTime`, `TimeSpan`, `Guid`, `char`, collections or tensors; typed collection null checks such as `p => p.Tags != null` remain supported.

Omitting both arguments makes the write unconditional. Explicit `condition: null`, `condition: default`, and `rawCondition: null` are also supported. For an existing string variable, change `condition: selector` to `rawCondition: selector` at the call site.

Use named arguments for conditions. `PutAsync(id, product, "product.price > 0")` selects the explicit-target instance method and treats the string as `documentType`; it now throws `ArgumentException` naming `documentType` before sending any HTTP request. A positional null or default in that slot also selects the document-type argument and fails local argument validation. Document types must match `[A-Za-z_][A-Za-z0-9_]*` and cannot be `true`, `false`, `and`, `or`, `not`, `null` or `id`, ignoring case.

## Write request options

Pass `requestOptions:` after the document arguments. The options-first write overloads are removed:

```csharp
// 3.x
await client.Documents.PutAsync(writeOptions, product.Id, product);

// 4.0
await client.Documents.PutAsync(product.Id, product,
    requestOptions: writeOptions);

await client.Documents.UpdateFieldsAsync<Product>(product.Id,
    update => update.Field(p => p.Price, FieldOp.Assign(49.99m)),
    condition: p => p.Price > 0,
    requestOptions: writeOptions,
    cancellationToken: cancellationToken);
```

Model-aware writes place `condition` and `rawCondition` together. Puts then take `create`; updates take `createIfMissing`. All writes end with `requestOptions` and `cancellationToken`, so deletes go directly from `rawCondition` to these arguments. Use named creation flags when migrating positional calls.

For example, a positional update creation flag moves behind the condition arguments:

```csharp
// 3.x
await client.Documents.UpdateFieldsAsync<Product>(product.Id, fields, true);

// 4.0
await client.Documents.UpdateFieldsAsync<Product>(product.Id, fields,
    createIfMissing: true);
```

Existing positional options and cancellation tokens may need to move to named arguments because their argument positions have changed:

```csharp
// 3.x
await client.Documents.DeleteAsync<Product>(product.Id, null, cancellationToken);

// 4.0
await client.Documents.DeleteAsync<Product>(product.Id,
    cancellationToken: cancellationToken);
```

## Mixed-feed conditions

The four generic `FeedOperation` factories use the same typed/native condition pair as model-aware writes. Keep the explicit target arguments before that pair and use named creation flags:

```csharp
// 3.x
var operation = FeedOperation.Update<Product>(product.Id,
    update => update.Field(p => p.Price, FieldOp.Assign(49.99m)),
    createIfMissing: true, condition: "product.price > 0");
```

In 4.0, use a typed predicate:

```csharp
var operation = FeedOperation.Update<Product>(product.Id,
    update => update.Field(p => p.Price, FieldOp.Assign(49.99m)),
    condition: p => p.Price > 0, createIfMissing: true);
```

To keep a native string, change `condition: selector` to `rawCondition: selector`. This applies to `Put<T>`, both `Update<T>` forms and `Delete<T>`. The non-generic `Update` and `Delete` factories retain their native string `condition` argument. Generic dictionary updates and deletes need `<Product>` when no argument supplies the model type.

An explicit `documentType` controls the typed condition's field qualifier and allows models without `[VespaDocument]`. An omitted namespace then uses the client default, even if the model has a namespace attribute. With no explicit target, the factory infers the schema and namespace as before. Use a predicate matching the intended payload type; a pre-typed base-class predicate can influence `Put<T>` inference and select the base model's metadata and serialization contract.

Values are captured at factory invocation, before the update callback, and the stored condition remains fixed during queuing and retries. For a lazy source, the factory may run during enumeration. Both conditions omitted or null remain unconditional; conflicting non-null conditions throw before the callback. See [Mixed feed conditions](feed.md#conditions).

## Homogeneous feed entries

Existing `FeedDocument<T>` and `BulkFieldUpdate` initializers, their string `Condition` properties and the `IFeedOperations` batch signatures remain available. New factories provide a typed construction path:

```csharp
var entry = FeedDocument.Create(product.Id, product,
    condition: stored => stored.Price > 0);

var update = BulkFieldUpdate.Create<Product>(product.Id,
    fields => fields.Field(p => p.Price, FieldOp.Assign(49.99m)),
    condition: stored => stored.Price > 0);
```

Use `documentType:` on a factory for an explicit schema, including models without `[VespaDocument]`. A typed condition without this argument uses the model's schema. Pass the same schema to the batch; the batch owns the namespace, including the client default when it is omitted. A raw or unconditional factory entry without an explicit schema records no expected schema and requires no attribute.

Factory-bound entries retain their schema through `with` copies, even when `Condition` changes. The built-in feed rejects a mismatching entry locally as one `FeedError` with no HTTP status and continues with the other entries. Vespa returns HTTP 412 when a submitted condition fails against the stored document. Construct a fresh entry for another schema. The condition is frozen before an update callback and remains stable across retries.

`ExpectedDocumentType` participates in record equality, so entries with different bindings can compare unequal despite matching public data; the generated `ToString()` omits this internal binding.

The expected schema is excluded from JSON. A deserialized `FeedDocument<T>` has the original raw DTO semantics; save the batch schema separately and rebuild a factory entry if schema validation is needed on replay. Custom `IFeedOperations` implementations keep their existing contract and do not inherit the built-in client's check. See [Per-document conditions](feed.md#per-document-conditions) for examples and serialization details.

## Selection mutations

Model-aware updates now take the field operations or callback first, followed by `selection`, `rawSelection`, cluster, continuation for a single page, request options and cancellation token:

```csharp
// 3.x
await client.Documents.UpdateBySelectionAsync<Product>(
    "product.price > 100",
    update => update.Field(p => p.Price, FieldOp.Multiply(0.9)),
    cluster: "content");
```

In 4.0, use a typed selection:

```csharp
await client.Documents.UpdateBySelectionAsync<Product>(
    update => update.Field(p => p.Price, FieldOp.Multiply(0.9)),
    selection: p => p.Price > 100,
    cluster: "content");
```

To keep the native selector, use this call instead:

```csharp
await client.Documents.UpdateBySelectionAsync<Product>(
    update => update.Field(p => p.Price, FieldOp.Multiply(0.9)),
    rawSelection: "product.price > 100",
    cluster: "content");
```

This applies to dictionary and builder updates, both automatic and single-page. New model-aware delete and copy methods follow the same selection convention. Copies take `cluster` and `destinationCluster` first; use their names when calling them. Keep `<Product>` on raw and dictionary calls so the client can resolve the model's document type and namespace.

These mutations require exactly one selection. Omitting it, passing null for both alternatives, supplying both non-null alternatives or passing a blank native string throws before the update callback or HTTP. Use `selection: p => true` to select every document of the model's exact type in its resolved namespace. Passing `rawSelection: "true"` sends native `true` to the addressed operation without adding an exact-type guard. Nonblank native strings are forwarded unchanged.

Typed selections are frozen before an update callback and remain fixed during automatic continuation. For manual pages, render and save `DocumentSelection.For<Product>(...)` with the token, then pass that string as `rawSelection:`. Existing native, non-generic selection methods retain their signatures. See [Selection-based operations](document-operations.md#selection-based-operations) for examples and count semantics.

## Visit selections

The three model-aware visit methods use `selection:` for a lambda and `rawSelection:` for a native string. Their optional arguments are ordered as selection, raw selection, cluster, continuation for a single page, visit options and cancellation token:

```csharp
// 3.x
var oldVisit = client.Documents.VisitAsync<Product>(
    selection: "product.price > 100");

// 4.0: keep the native selector
var nativeVisit = client.Documents.VisitAsync<Product>(
    rawSelection: "product.price > 100");

// 4.0: use a typed predicate
var typedVisit = client.Documents.VisitAsync<Product>(
    selection: p => p.Price > 100);
```

This applies to `VisitAsync`, `VisitPageAsync` and `VisitJsonlAsync`. Use named arguments when migrating positional cluster, continuation, options or cancellation arguments. For example, `VisitPageAsync<Product>("archive", cluster: "content")` visits the explicit document type `archive`. Use `rawSelection:` to send a native predicate with the type inferred from `Product`.

Typed visit filters are rendered when the method is called and stay fixed during automatic continuation. For manual resume, save `DocumentSelection.For<Product>(...)` with the continuation token. Pass the saved string as `rawSelection:` to the model-aware `VisitPageAsync<Product>`, or as `selection:` to the native root `VisitAllPageAsync<Result>`. The two-type root overload takes a lambda. Calling a lambda again reads its captured values again.

Root visits can filter one model and return another through `VisitAllAsync<Result, Product>(cluster, selection: p => p.Price > 100)` or `VisitAllPageAsync<Result, Product>`. The result type needs no document attribute. Typed selectors restrict the entire predicate to the exact document type across namespaces; this also applies to `true` and negations and excludes derived document types. Existing native root methods keep their string `selection` argument and can still visit all types. [Visit examples](document-operations.md#visit--iterate) cover saved selectors and explicit targets.

## Streaming-search selections

`WithStreamingGroups("tenant-a", "tenant-b")` sets a native disjunction of `id.group` equalities, allowing Vespa to restrict visiting to those groups. Keep content filters in YQL. `WithStreamingSelection<Product>(p => p.Price > 100)` builds a document selector scoped to the model's exact document type, but can scan all groups. It does not change the YQL source, namespace or result model.

These helpers require `using Vespa.Query;`, return the same request and replace only `StreamingSelection`. Values are frozen when called. They reject an existing `StreamingGroupName` and conflicting streaming location keys in `CustomParameters` before changing the request. The native `StreamingSelection` setter remains available. No existing search signature has changed.

The group helper removes ordinal duplicates and escapes the names. It rejects more than one distinct group name ending in a backslash because Vespa's selector parser cannot safely combine them; use separate requests. `StreamingMaxBucketsPerVisit` retains its name and JSON binding, with corrected documentation: it controls backend bucket concurrency, not a total bucket or coverage limit. See [Native streaming search](search.md#native-streaming-search) for examples and input rules.

## Document field sets

`DocumentFieldSet.For<Product>(p => p.Name, p => p.Price)` builds a native field-list string using mapped property names. `ForDocumentType<PlainProduct>("product", p => p.Price)` uses an explicit document type as the field-list qualifier without requiring a document attribute. Use `using Vespa.Documents;` and pass the result as `fieldSet:` on GET by ID, group or number, or on `GetManyAsync`. For visits, assign it to `VisitOptions.FieldSet`.

Built-in strings such as `[document]`, `[all]` and `[id]` can also be passed as `fieldSet:`. Replace the deprecated `product:[document]` spelling with `[document]`. See [Document field sets](document-operations.md#document-field-sets) for examples and validation rules.

## Read request options

Move options from the first argument of the read extensions to `requestOptions:` after the address or list of IDs. Pass `fieldSet:` by name when inferring a target from the model. A positional string immediately after the address is interpreted as an explicit document type, even with `requestOptions:` present.

```csharp
// 3.x
var oldRead = client.Documents.GetAsync<Product>(options, id, fieldSet: fields, cancellationToken: token);

// 4.0
var read = client.Documents.GetAsync<Product>(id, fieldSet: fields, requestOptions: options, cancellationToken: token);
var batch = client.Documents.GetManyAsync<Product>(ids, maxConcurrency: 4,
    fieldSet: fields, requestOptions: options, cancellationToken: token);
```

Apply the same migration to `GetByGroupAsync` and `GetByNumberAsync`. For an explicit batch target, pass `documentType:` and, if needed, `@namespace:`. `GetManyAsync` applies the field set to each individual GET and omits missing documents. Name any previously positional cancellation token and rebuild callers of the read extensions. [Request options](document-operations.md#request-options)

## Typed field selectors

Typed updates, conditions and selections require a direct, public property with its own document JSON binding. They reject document IDs, extra-fields containers, unconditionally ignored properties and ambiguous hidden members. Typed updates that previously accepted these properties now throw before sending a request.

The resolved field name must also match the name used in document JSON. Declare `[VespaField(Name = "...")]` directly on an override if it inherits a different alias, and use this attribute for custom JSON names. It takes precedence over `[JsonPropertyName]`. A remaining mismatch throws `NotSupportedException`; native conditions and explicit update field names remain available. Typed YQL can still query fields that JSON ignores, such as fields populated by another writer.

## Document retention in generated packages

Existing attribute selections continue to work. To supply a cutoff when building the package, use the options returned by `WithDocumentSelection<T>(...)`. For a moving cutoff, put a native expression using `now()` in `ApplicationPackageOptions.DocumentSelections`. The default feed route evaluates either selection, and the SDK enables garbage collection in the generated package. Choose one source per document type and pass the options to the existing generation or deployment method. [Document expiry and routing examples](schema.md#document-expiry-selection)

## Application-package resources

Files declared with `SourcePath` on `[VespaOnnxModel]` or `[VespaConstant]` are now copied after the schema's ZIP entry has been closed. A bundled resource that shares a destination with a generated schema, `services.xml` or generated `hosts.xml` throws `InvalidOperationException`. Use a separate asset path, or `CustomServicesXml` for service configuration. Public package-generation signatures are unchanged; see [Bundled files](schema.md#bundled-files) for path resolution and duplicate-resource precedence.

## Rank profile functions

Replace `VespaRankProfileAttribute.Functions` with one `[VespaRankFunction]` attribute per function on the same model. Keep the original signature and expression and set `Profile` to the name of the old rank profile.

```csharp
[VespaRankProfile("semantic", FirstPhase = "boost(2)")]
[VespaRankFunction("boost(factor)", "attribute(quality) * factor", Profile = "semantic")]
```

Recompile every assembly that declares `Functions = ...`, including model libraries and plugins. A model compiled with that named attribute argument can throw `CustomAttributeFormatException` when the 4.0 schema generator reads its rank profiles. [Ranking functions](ranking.md#code-first-rank-profiles)

## Tensor distance metrics

Replace `DistanceMetric.InnerProduct` in source code and `"InnerProduct"` in saved settings with `PrenormalizedAngular`. Its numeric value is `7`. The schema generator still accepts the old numeric value `6`, including in precompiled `[VespaTensor]` attributes, and emits `prenormalized-angular`. If your application validates saved values with `Enum.IsDefined`, convert `6` to `7` first.

Use `PrenormalizedAngular` for query and document vectors normalized to the same nonzero L2 norm. `DotProduct` remains `3` and selects Vespa's maximum inner product search, where vector magnitudes affect the score. [Vespa distance metrics](https://docs.vespa.ai/en/reference/schemas/schemas.html#distance-metric)

## Attributes on C# fields

Move `[VespaField]`, `[VespaTensor]` and `[VespaId]` from fields to public properties. If you used a `field:` attribute target on an auto-property, apply the attribute to the property itself. Keep any field alias or tensor type. Use a writable `string` property with `[VespaId]` to receive the document ID from Vespa.

When you move a field to a property, review the generated schema before deploying. The generator can now include that member. Rebuild callers that access a public field you converted to a property. If `[VespaField]` only supplied a JSON alias, keep the field with `[JsonInclude]` and use `[JsonPropertyName]` for the alias. This target change preserves the JSON behavior of already compiled models.

## Tensor field declarations

Declare the tensor type on a document property with `[VespaTensor("tensor<float>(x[384])")]`, using the actual cell type and dimensions. If the property already has both attributes, remove only `FieldType = VespaFieldType.Tensor` and keep `[VespaField]` to preserve its naming and indexing options. [Field configuration](schema.md#field-configuration)

Precompiled models with both attributes still generate the same schema. The numeric value `11` is reserved, and all remaining `VespaFieldType` values retain their numbers.

## Tensor JSON formats

Remove `PreferredFormat = ...` from `[VespaTensor]` attributes and keep the tensor type declaration. The old setting had no effect on serialization. To choose how to write a `VespaTensor`, use the factory for your data, such as `FromDenseValues` for a dense vector or `FromMappedValues` for a single mapped dimension.

Recompile every model assembly that specifies `PreferredFormat`. The 4.0 schema generator throws `CustomAttributeFormatException` when it reads a precompiled attribute with that named argument.

## Explicit targets and feeds

The low-level methods on `IDocumentOperations` still accept a native string named `condition` when you provide `documentType` explicitly:

```csharp
await client.Documents.PutAsync(product.Id, product,
    documentType: "product", @namespace: "catalog",
    condition: "product.price > 0");
```

The non-generic feed factories and native selection APIs retain their string parameters. Use `DocumentCondition.For<Product>(p => p.Price > 0)` to pre-render a write condition when the target schema matches the model's annotation, then pass that string through the appropriate native argument or a generic factory's `rawCondition:`. For native selections and visits restricted to that document type, use `DocumentSelection.For<Product>(p => p.Price > 0)`. A condition alone adds no type filter: on a raw root visit, `DocumentCondition.For<Product>(p => true)` would visit every type. See [Conditional writes](document-operations.md#conditional-writes) for supported expressions and evaluation rules.

Rebuild applications and libraries that use the model-aware document extensions or the four generic mixed-feed factories. Their method signatures have changed, including unconditional calls. The methods declared by `IDocumentOperations` and `IFeedOperations` keep their current signatures, so custom implementations of these interfaces need no changes for these features.

## Open lower bounds in grouping buckets

`GroupingBucket.Numeric` and `GroupingBucket.Text` now render an exclusive lower bound when you set `fromInclusive: false`. Check queries that depended on the earlier inclusive result and set `fromInclusive: true` to keep that result.

## Named client registration

`Vespa.NET` is the HTTP client name reserved for the unnamed registration. Passing that exact name to the named `AddVespaClient` overload now throws `ArgumentException` during registration. If you used it as a keyed client name, choose a different name and update keyed lookups, health checks and any `IHttpClientFactory` calls intended for that client.

## Circuit breaker minimum throughput

Rename `CircuitBreakerFailureThreshold` to `CircuitBreakerMinimumThroughput` in object initializers, configuration keys, and environment variables used for configuration binding. Keep the same integer value and rebuild assemblies that access the property.

The default is still five sampled HTTP attempts. The default DI circuit breaker opens on a handled failure once it has sampled the minimum number of attempts and at least half have failed. [Client options](configuration.md#vespaclientoptions)

## Search pagination with WithYql

`WithYql(builder)` now copies the builder's explicit limit and offset to `Hits` and `Offset`. If the request already has `Offset=20` and the builder sets only `.Limit(10)`, the single search returns up to ten hits starting at offset 20. Set `.Offset(0)` to start at the beginning. Assign either request property after `WithYql` to override the builder value. [Search pagination](search.md#paged-search)

## Sort direction

`OrderBy` and `OrderByRaw` use `SortDirection`. Replace `descending: true` with `SortDirection.Descending` and `descending: false` with `SortDirection.Ascending`. Omitting the direction selects ascending order.

`RankingBuilder.MatchPhase` uses nullable `SortDirection`. Replace `ascending: true` with `direction: SortDirection.Ascending` and `ascending: false` with `direction: SortDirection.Descending`. Omit the direction to use Vespa's configured order. If you set `RankingConfig.MatchPhaseAscending` directly, it still accepts the nullable boolean used by the Query API.

Rebuild callers of these builder methods, including calls that omit the direction.

## Tensor modify operations

`FieldOp.Modify`, `ModifyCells`, and `ModifyBlocks` take `TensorModifyOperation`. Replace `"replace"`, `"add"`, and `"multiply"` with `TensorModifyOperation.Replace`, `Add`, and `Multiply` respectively, then rebuild callers of those factories. Vespa receives the same lowercase operation names. [Field-level updates](document-operations.md#field-level-updates)

## Nearest-neighbor search mode

`NearestNeighbor` and `HybridSearch` in the YQL builder accept `NearestNeighborMode?` in place of `bool? approximate`. Replace `approximate: false` with `mode: NearestNeighborMode.Exact`, and `approximate: true` with `mode: NearestNeighborMode.Approximate`. Omit `mode` to keep the server default. Rebuild callers of these methods. [Nearest-neighbor search](search.md#nearest-neighbor-search)

## Term annotations

`ContainsAnnotated` takes a `YqlTermAnnotations` object. Move options such as `filter: true, stem: false` to `new() { Filter = true, Stem = false }`. Use `Contains(value)` for an unannotated term. Pass a nonblank term and a non-null options object to `ContainsAnnotated`. Rebuild callers of the former signature.

- Replace `termId: 4` by itself with `new() { TermId = 4 }`.
- Replace `termId: 4, connectivityWeight: 0.8` with `new() { Connectivity = new(4, 0.8) }`. In this call, `4` identifies the other term.
- A standalone `connectivityWeight` was ignored. Drop it or supply a target ID and weight in `Connectivity` to create a link.

Set both `TermId` and `Connectivity` to identify a term that also links to another term. [Annotated terms](yql-builder.md#field-predicates)
