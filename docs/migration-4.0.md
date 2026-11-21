# Migrating to Vespa.NET 4.0

This guide covers the planned 4.0 write API. The release date and the rest of the major's scope are still being decided. Model-aware calls require a `[VespaDocument]` attribute; calls with an explicit document type can use an undecorated model.

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

Omitting both arguments makes the write unconditional. Explicit `condition: null`, `condition: default`, and `rawCondition: null` are also supported. For an existing string variable, change `condition: selector` to `rawCondition: selector` at the call site.

Use named arguments for conditions. `PutAsync(id, product, "value")` selects the explicit-target instance method and treats the string as `documentType`. A positional null or default in that slot also selects the document-type argument and fails validation.

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

Model-aware writes place `condition` and `rawCondition` together. Puts then take `create`; updates take `createIfMissing`. All writes end with `requestOptions` and `cancellationToken`, so deletes go directly from `rawCondition` to these arguments. Use named creation flags when migrating positional calls. Read methods, including `GetAsync`, addressed reads and `GetManyAsync`, retain their existing options-first overloads.

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

## Typed field selectors

Typed updates and conditions require a direct, public property with its own document JSON binding. They reject document IDs, extra-fields containers, unconditionally ignored properties and ambiguous hidden members. Typed updates that previously accepted these properties now throw before sending a request.

The resolved field name must also match the name used in document JSON. Declare `[VespaField(Name = "...")]` directly on an override if it inherits a different alias, and use this attribute for custom JSON names. It takes precedence over `[JsonPropertyName]`. A remaining mismatch throws `NotSupportedException`; native conditions and explicit update field names remain available. Typed YQL can still query fields that JSON ignores, such as fields populated by another writer.

## Explicit targets and feeds

The low-level methods on `IDocumentOperations` still accept a native string named `condition` when you provide `documentType` explicitly:

```csharp
await client.Documents.PutAsync(product.Id, product,
    documentType: "product", @namespace: "catalog",
    condition: "product.price > 0");
```

Feed factories and selection APIs also retain their string parameters. Use `DocumentCondition.For<Product>(p => p.Price > 0)` to produce a selector for these APIs when its target schema matches the model's `[VespaDocument]` annotation. See [Conditional writes](document-operations.md#conditional-writes) for supported expressions and evaluation rules.

Rebuild applications and libraries that use the model-aware write extensions. Their method signatures have changed, including calls that passed no condition. The methods declared by `IDocumentOperations` keep their current signatures, so custom implementations of that interface need no changes for this feature.
