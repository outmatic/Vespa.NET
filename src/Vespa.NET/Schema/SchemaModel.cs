using System.Reflection;
using Vespa.Models.Attributes;

namespace Vespa.Models.Schema;

// ── Intermediate schema model ─────────────────────────────────────────────────
// Built in a single reflection pass by SchemaModelBuilder, rendered by the small
// emitters in VespaSchemaBuilder. All validation happens while building the model.

/// <summary>Structural shape of a field, driving indexing/struct-field emission.</summary>
internal enum FieldShape
{
    /// <summary>Primitive, array-of-primitive, weightedset, raw, uri, reference, …</summary>
    Simple,

    /// <summary>A single [VespaStruct] value.</summary>
    Struct,

    /// <summary>An array of [VespaStruct] values.</summary>
    StructCollection,

    /// <summary>A map (any key/value combination that is not a weightedset).</summary>
    Map
}

internal sealed record SchemaStructModel(string Name, IReadOnlyList<(string Name, string VespaType)> Fields);

internal sealed record RankFunctionModel(string Signature, string Expression);

internal sealed class RankProfileModel
{
    public required string Name { get; init; }
    public string? Inherits { get; init; }
    public string? FirstPhase { get; init; }
    public string? SecondPhase { get; init; }
    public int SecondPhaseRerankCount { get; init; }
    public string? GlobalPhase { get; init; }
    public int GlobalPhaseRerankCount { get; init; }
    public string? MatchFeatures { get; init; }
    public string? SummaryFeatures { get; init; }
    public string? DiversityAttribute { get; init; }
    public int DiversityMinGroups { get; init; }
    public List<RankFunctionModel> Functions { get; } = [];
    public bool EmitInputs { get; set; }
}

internal sealed class SchemaFieldModel
{
    public required string Name { get; init; }
    public required string VespaType { get; init; }
    public string? Indexing { get; init; }
    public string? Match { get; init; }
    public string? Stemming { get; init; }
    public bool Bolding { get; init; }
    public bool FastSearch { get; init; }
    public string? Normalizing { get; init; }
    public string? Rank { get; init; }
    public string? RankTypeName { get; init; }
    public IReadOnlyList<(string Path, string Indexing)> StructFields { get; init; } = [];

    // Tensor-only
    public bool IsTensor { get; init; }
    public string? DistanceMetric { get; init; }
    public bool HnswIndex { get; init; }
    public int MaxLinksPerNode { get; init; }
    public int NeighborsToExploreAtInsert { get; init; }

    // Predicate-only
    public int? PredicateArity { get; init; }
    public long? PredicateLowerBound { get; init; }
    public long? PredicateUpperBound { get; init; }
}

internal sealed class SchemaModel
{
    public required string DocType { get; init; }
    public string? Inherits { get; init; }
    public required IReadOnlyList<SchemaStructModel> Structs { get; init; }
    public required IReadOnlyList<SchemaFieldModel> Fields { get; init; }
    public required IReadOnlyList<(string QueryName, string TensorType)> TensorInputs { get; init; }
    public required IReadOnlyList<VespaImportFieldAttribute> ImportFields { get; init; }
    public required IReadOnlyList<VespaConstantAttribute> Constants { get; init; }
    public required IReadOnlyList<VespaOnnxModelAttribute> OnnxModels { get; init; }
    public required IReadOnlyList<VespaFieldSetAttribute> FieldSets { get; init; }
    public required IReadOnlyList<VespaDocumentSummaryAttribute> DocumentSummaries { get; init; }
    public required IReadOnlyList<RankProfileModel> RankProfiles { get; init; }
}

// ── CLR → Vespa type helpers (stateless) ─────────────────────────────────────

internal static class VespaTypeMapper
{
    private static readonly HashSet<string> _primitiveVespaTypes =
        ["bool", "string", "int", "long", "byte", "float", "double"];

    /// <summary>Vespa primitive types — the only types allowed as struct-field attributes and map keys.</summary>
    internal static bool IsPrimitiveVespaType(string vespaType) => _primitiveVespaTypes.Contains(vespaType);

    /// <summary>
    /// Unwraps single-dimensional arrays and the common (read-only) collection shapes
    /// — <c>List&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>,
    /// <c>IEnumerable&lt;T&gt;</c>, <c>IReadOnlyList&lt;T&gt;</c>, <c>IReadOnlyCollection&lt;T&gt;</c>,
    /// <c>HashSet&lt;T&gt;</c>, <c>ISet&lt;T&gt;</c>, <c>IReadOnlySet&lt;T&gt;</c> — to their element type.
    /// </summary>
    internal static bool TryGetElementType(Type type, out Type elementType)
    {
        if (type.IsArray && type.GetArrayRank() == 1)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType)
        {
            var gtd = type.GetGenericTypeDefinition();
            if (gtd == typeof(List<>) || gtd == typeof(IList<>) || gtd == typeof(ICollection<>)
                || gtd == typeof(IEnumerable<>) || gtd == typeof(IReadOnlyList<>) || gtd == typeof(IReadOnlyCollection<>)
                || gtd == typeof(HashSet<>) || gtd == typeof(ISet<>) || gtd == typeof(IReadOnlySet<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        elementType = null!;
        return false;
    }

    /// <summary>Unwraps <c>Dictionary&lt;K,V&gt;</c>, <c>IDictionary&lt;K,V&gt;</c> and <c>IReadOnlyDictionary&lt;K,V&gt;</c>.</summary>
    internal static bool TryGetDictionaryTypes(Type type, out Type keyType, out Type valueType)
    {
        if (type.IsGenericType)
        {
            var gtd = type.GetGenericTypeDefinition();
            if (gtd == typeof(Dictionary<,>) || gtd == typeof(IDictionary<,>) || gtd == typeof(IReadOnlyDictionary<,>))
            {
                var args = type.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                return true;
            }
        }

        keyType = null!;
        valueType = null!;
        return false;
    }

    /// <summary>
    /// Maps a scalar CLR type to its Vespa type name, or returns null for
    /// non-scalar types (collections, structs). Types that would silently
    /// corrupt data throw instead of guessing:
    /// C# <c>byte</c> (0–255 does not fit Vespa's signed byte), <c>ulong</c>
    /// (no unsigned 64-bit in Vespa) and enums (serialized as numbers by default).
    /// <c>Guid</c> and <c>char</c> map to <c>string</c> because System.Text.Json
    /// serializes them as JSON strings.
    /// </summary>
    internal static string? MapScalar(Type type, string context)
    {
        if (type.IsEnum)
            throw new NotSupportedException(
                $"Cannot map enum type '{type.Name}' ({context}): System.Text.Json serializes enums " +
                "as numbers by default and Vespa has no enum type. Use an int/long property, or serialize " +
                "the enum as a string (JsonStringEnumConverter on the property) and declare the field with " +
                "FieldType = VespaFieldType.String.");

        if (type == typeof(byte))
            throw new NotSupportedException(
                $"Cannot map C# 'byte' ({context}): it is unsigned (0..255) while Vespa 'byte' is signed " +
                "(-128..127), so values 128..255 would fail at feed time. Use 'sbyte' for a Vespa byte, " +
                "or 'short'/'int' for the full 0..255 range.");

        if (type == typeof(ulong))
            throw new NotSupportedException(
                $"Cannot map 'ulong' ({context}): Vespa has no unsigned 64-bit type. " +
                "Use 'long' if the values fit, or a string field.");

        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(double)) return "double";
        if (type == typeof(float)) return "float";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte[])) return "raw"; // fed as base64, matching System.Text.Json
        if (type == typeof(decimal)) return "double";
        if (type == typeof(short) || type == typeof(ushort)) return "int";
        if (type == typeof(uint)) return "long";
        if (type == typeof(sbyte)) return "byte";
        if (type == typeof(Guid)) return "string"; // serialized as a JSON string
        if (type == typeof(char)) return "string"; // serialized as a JSON string

        return null;
    }
}

// ── Reflection pass ──────────────────────────────────────────────────────────

/// <summary>
/// Builds a <see cref="SchemaModel"/> from an attributed CLR type in a single
/// reflection pass, performing all schema validation up front.
/// </summary>
internal sealed class SchemaModelBuilder
{
    private readonly List<SchemaStructModel> _structs = [];
    private readonly Dictionary<Type, SchemaStructModel> _structIndex = [];
    private readonly HashSet<Type> _structsInProgress = [];

    private sealed record ResolvedFieldType(
        string VespaType,
        FieldShape Shape,
        Type? StructType = null,
        string? MapValueType = null,
        Type? MapValueStructType = null);

    public static SchemaModel Build(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var docAttr = type.GetCustomAttribute<VespaDocumentAttribute>()
            ?? throw new InvalidOperationException(
                $"Type '{type.Name}' must be decorated with [VespaDocument].");

        return new SchemaModelBuilder().BuildCore(type, docAttr);
    }

    private SchemaModel BuildCore(Type type, VespaDocumentAttribute docAttr)
    {
        var fields = new List<SchemaFieldModel>();
        var tensorInputs = new List<(string QueryName, string TensorType)>();

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // [VespaExtraFields] is a catch-all bag for unmapped fields — skip in schema generation
            if (prop.GetCustomAttribute<VespaExtraFieldsAttribute>() is not null)
                continue;

            var tensorAttr = prop.GetCustomAttribute<VespaTensorAttribute>();
            var fieldAttr = prop.GetCustomAttribute<VespaFieldAttribute>();

            if (tensorAttr is not null)
                fields.Add(BuildTensorField(prop, tensorAttr, fieldAttr, tensorInputs));
            else if (fieldAttr is not null)
                fields.Add(BuildRegularField(prop, fieldAttr));
        }

        return new SchemaModel
        {
            DocType = docAttr.DocumentType,
            Inherits = docAttr.Inherits,
            Structs = _structs,
            Fields = fields,
            TensorInputs = tensorInputs,
            ImportFields = [.. type.GetCustomAttributes<VespaImportFieldAttribute>()],
            Constants = [.. type.GetCustomAttributes<VespaConstantAttribute>()],
            OnnxModels = [.. type.GetCustomAttributes<VespaOnnxModelAttribute>()],
            FieldSets = [.. type.GetCustomAttributes<VespaFieldSetAttribute>()],
            DocumentSummaries = [.. type.GetCustomAttributes<VespaDocumentSummaryAttribute>()],
            RankProfiles = BuildRankProfiles(type, tensorInputs)
        };
    }

    // ── Fields ────────────────────────────────────────────────────────────────

    private static string ResolveFieldName(PropertyInfo prop, VespaFieldAttribute? fieldAttr)
    {
        var name = fieldAttr?.Name ?? VespaNaming.ToSnakeCase(prop.Name);
        if (name.Equals("id", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Field '{prop.Name}' on type '{prop.DeclaringType?.Name}' resolves to Vespa field name 'id', " +
                "which conflicts with the document identifier. Use [VespaId] for the document ID, " +
                "or choose a different field name.");
        return name;
    }

    private SchemaFieldModel BuildRegularField(PropertyInfo prop, VespaFieldAttribute fieldAttr)
    {
        var name = ResolveFieldName(prop, fieldAttr);
        var context = $"property '{prop.Name}' on type '{prop.DeclaringType?.Name}'";
        var resolved = ResolveDeclaredType(prop, fieldAttr, context);
        var mode = fieldAttr.IndexingMode;

        string? indexing = null;
        IReadOnlyList<(string, string)> structFields = [];
        int? predicateArity = null;
        long? predicateLower = null;
        long? predicateUpper = null;

        if (resolved.Shape is not FieldShape.Simple)
        {
            // Struct, array-of-struct and map fields: attribute/index are invalid on the
            // outer field; Summary maps to `indexing: summary`, Attribute to struct-field blocks.
            var kind = resolved.Shape is FieldShape.Map ? "map" : "struct";
            if ((mode & IndexingMode.Index) != 0)
                throw new InvalidOperationException(
                    $"IndexingMode.Index is not supported for {kind} fields in indexed search ({context}). " +
                    "Use IndexingMode.Summary, optionally combined with IndexingMode.Attribute to generate " +
                    "struct-field attribute blocks for primitive-typed struct members.");
            if (fieldAttr.FastSearch)
                throw new InvalidOperationException(
                    $"FastSearch cannot be set on a {kind} field ({context}): fast-search applies to " +
                    "attribute fields only, and the outer field of a struct/map cannot be an attribute.");

            // Vespa's ComplexFieldsWithStructFieldAttributesValidator only accepts
            // struct-field attributes on array-of-struct and map fields; a single
            // struct field with attributed members fails deployment.
            if ((mode & IndexingMode.Attribute) != 0 && resolved.Shape is FieldShape.Struct)
                throw new InvalidOperationException(
                    $"IndexingMode.Attribute is not supported for single struct fields in indexed search ({context}): " +
                    "Vespa allows struct-field attributes only on array-of-struct and map fields. " +
                    "Set IndexingMode = IndexingMode.Summary explicitly (the default AttributeSummary includes Attribute).");

            if ((mode & IndexingMode.Summary) != 0)
                indexing = "summary";
            if ((mode & IndexingMode.Attribute) != 0)
                structFields = BuildStructFieldEntries(resolved);
        }
        else if (resolved.VespaType == "predicate")
        {
            if (fieldAttr.PredicateArity < 1)
                throw new InvalidOperationException(
                    $"Predicate field '{name}' ({context}) requires PredicateArity >= 1 — " +
                    "Vespa mandates `index { arity: N }` on predicate fields.");
            if ((mode & IndexingMode.Index) != 0)
                throw new InvalidOperationException(
                    $"IndexingMode.Index is not supported for predicate fields ({context}); " +
                    "predicates use an implicit attribute-backed index configured via PredicateArity.");
            if ((mode & IndexingMode.Attribute) == 0)
                throw new InvalidOperationException(
                    $"Predicate field '{name}' ({context}) must include IndexingMode.Attribute — " +
                    "predicate fields are stored as attributes.");

            indexing = BuildIndexingString(mode);
            predicateArity = fieldAttr.PredicateArity;
            if (fieldAttr.PredicateLowerBound != long.MinValue)
                predicateLower = fieldAttr.PredicateLowerBound;
            if (fieldAttr.PredicateUpperBound != long.MaxValue)
                predicateUpper = fieldAttr.PredicateUpperBound;
        }
        else if (mode != IndexingMode.None)
        {
            indexing = BuildIndexingString(mode);
        }

        if (fieldAttr.PredicateArity != 0 && resolved.VespaType != "predicate")
            throw new InvalidOperationException(
                $"PredicateArity is set on non-predicate field '{name}' ({context}); " +
                "it only applies to FieldType = VespaFieldType.Predicate.");

        var (rank, rankTypeName) = fieldAttr.RankType switch
        {
            RankType.Filter => ("filter", (string?)null),
            RankType.Default => ("normal", (string?)null),
            RankType.Identity => ((string?)null, "identity"),
            RankType.About => ((string?)null, "about"),
            RankType.Tags => ((string?)null, "tags"),
            RankType.Empty => ((string?)null, "empty"),
            _ => ((string?)null, (string?)null)
        };

        return new SchemaFieldModel
        {
            Name = name,
            VespaType = resolved.VespaType,
            Indexing = indexing,
            Match = ToVespaMatchName(fieldAttr.MatchMode),
            Stemming = ToVespaStemmingName(fieldAttr.Stemming),
            Bolding = fieldAttr.Bolding,
            FastSearch = fieldAttr.FastSearch,
            Normalizing = fieldAttr.Normalizing,
            Rank = rank,
            RankTypeName = rankTypeName,
            StructFields = structFields,
            PredicateArity = predicateArity,
            PredicateLowerBound = predicateLower,
            PredicateUpperBound = predicateUpper
        };
    }

    private SchemaFieldModel BuildTensorField(
        PropertyInfo prop,
        VespaTensorAttribute tensorAttr,
        VespaFieldAttribute? fieldAttr,
        List<(string QueryName, string TensorType)> tensorInputs)
    {
        var name = ResolveFieldName(prop, fieldAttr);
        var context = $"property '{prop.Name}' on type '{prop.DeclaringType?.Name}'";
        var typeStr = tensorAttr.GetTensorTypeString();
        var mode = fieldAttr?.IndexingMode ?? IndexingMode.None;

        var wantsIndex = tensorAttr.EnableIndex || (mode & IndexingMode.Index) != 0;
        if (wantsIndex && tensorAttr.IndexType == TensorIndexType.None)
            throw new InvalidOperationException(
                $"Tensor field '{name}' ({context}) requests an index (EnableIndex or IndexingMode.Index) " +
                "but IndexType is TensorIndexType.None. Use TensorIndexType.Hnsw, or drop the index request " +
                "for exact (brute-force) nearest neighbor search.");

        var wantsSummary = tensorAttr.IncludeInSummary || (mode & IndexingMode.Summary) != 0;

        var indexingParts = new List<string> { "attribute" };
        if (wantsIndex)
            indexingParts.Add("index");
        if (wantsSummary)
            indexingParts.Add("summary");

        tensorInputs.Add(($"query(query_{name})", typeStr));

        return new SchemaFieldModel
        {
            Name = name,
            VespaType = typeStr,
            Indexing = string.Join(" | ", indexingParts),
            IsTensor = true,
            DistanceMetric = ToVespaMetricName(tensorAttr.DistanceMetric),
            HnswIndex = wantsIndex,
            MaxLinksPerNode = tensorAttr.MaxLinksPerNode,
            NeighborsToExploreAtInsert = tensorAttr.NeighborsToExploreAtInsert
        };
    }

    private List<(string, string)> BuildStructFieldEntries(ResolvedFieldType resolved)
    {
        var entries = new List<(string, string)>();
        switch (resolved.Shape)
        {
            case FieldShape.Map:
                // Map entries are structs with `key`/`value` members; keys are validated primitive.
                entries.Add(("key", "attribute"));
                if (resolved.MapValueType is { } valueType && VespaTypeMapper.IsPrimitiveVespaType(valueType))
                    entries.Add(("value", "attribute"));
                else if (resolved.MapValueStructType is { } valueStruct)
                    entries.AddRange(PrimitiveStructMembers(valueStruct).Select(m => ($"value.{m}", "attribute")));
                break;

            case FieldShape.StructCollection:
                entries.AddRange(PrimitiveStructMembers(resolved.StructType!).Select(m => (m, "attribute")));
                break;
        }

        return entries;

        IEnumerable<string> PrimitiveStructMembers(Type structType) =>
            _structIndex[structType].Fields
                .Where(f => VespaTypeMapper.IsPrimitiveVespaType(f.VespaType))
                .Select(f => f.Name);
    }

    // ── Type resolution ───────────────────────────────────────────────────────

    private ResolvedFieldType ResolveDeclaredType(PropertyInfo prop, VespaFieldAttribute fieldAttr, string context)
    {
        if (fieldAttr.FieldType == VespaFieldType.Reference)
        {
            var target = fieldAttr.ReferenceDocumentType
                ?? throw new InvalidOperationException(
                    $"Field '{prop.Name}' has FieldType=Reference but ReferenceDocumentType is not set.");
            return new ResolvedFieldType($"reference<{target}>", FieldShape.Simple);
        }

        return fieldAttr.FieldType switch
        {
            VespaFieldType.None => ResolveClrType(prop.PropertyType, context),
            VespaFieldType.MapStringString => new ResolvedFieldType("map<string,string>", FieldShape.Map, MapValueType: "string"),
            VespaFieldType.MapStringInt => new ResolvedFieldType("map<string,int>", FieldShape.Map, MapValueType: "int"),
            _ => new ResolvedFieldType(ToVespaTypeName(fieldAttr.FieldType), FieldShape.Simple)
        };
    }

    private ResolvedFieldType ResolveClrType(Type clrType, string context)
    {
        var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

        // Scalars first: byte[] → raw must win over array unwrapping.
        if (VespaTypeMapper.MapScalar(type, context) is { } scalar)
            return new ResolvedFieldType(scalar, FieldShape.Simple);

        if (type.GetCustomAttribute<VespaStructAttribute>() is not null)
            return new ResolvedFieldType(CollectStruct(type).Name, FieldShape.Struct, type);

        if (VespaTypeMapper.TryGetDictionaryTypes(type, out var keyType, out var valueType))
        {
            var key = ResolveClrType(keyType, context).VespaType;
            if (!VespaTypeMapper.IsPrimitiveVespaType(key))
                throw new NotSupportedException(
                    $"Cannot map dictionary key type '{keyType.Name}' ({context}): Vespa map keys must be primitive.");

            var unwrappedValue = Nullable.GetUnderlyingType(valueType) ?? valueType;
            if (unwrappedValue.GetCustomAttribute<VespaStructAttribute>() is not null)
            {
                var structModel = CollectStruct(unwrappedValue);
                return new ResolvedFieldType(
                    $"map<{key},{structModel.Name}>", FieldShape.Map, MapValueStructType: unwrappedValue);
            }

            var value = ResolveClrType(valueType, context).VespaType;
            return key == "string" && value == "int"
                ? new ResolvedFieldType("weightedset<string>", FieldShape.Simple)
                : new ResolvedFieldType($"map<{key},{value}>", FieldShape.Map, MapValueType: value);
        }

        if (VespaTypeMapper.TryGetElementType(type, out var elementType))
        {
            var element = Nullable.GetUnderlyingType(elementType) ?? elementType;
            if (element.GetCustomAttribute<VespaStructAttribute>() is not null)
                return new ResolvedFieldType(
                    $"array<{CollectStruct(element).Name}>", FieldShape.StructCollection, element);

            return new ResolvedFieldType(
                $"array<{ResolveClrType(element, context).VespaType}>", FieldShape.Simple);
        }

        throw new NotSupportedException(
            $"Cannot map CLR type '{type.Name}' ({context}) to a Vespa field type. " +
            "Set VespaFieldAttribute.FieldType explicitly, or use a supported type: string, bool, int, " +
            "long, float, double, decimal, short, ushort, uint, sbyte, Guid, char, byte[] (raw), " +
            "collections/dictionaries of these, or a [VespaStruct]-annotated type.");
    }

    private SchemaStructModel CollectStruct(Type structType)
    {
        if (_structIndex.TryGetValue(structType, out var existing))
            return existing;

        if (!_structsInProgress.Add(structType))
            throw new NotSupportedException(
                $"Struct type '{structType.Name}' is recursive (directly or via a nested struct); " +
                "Vespa schemas cannot express recursive struct types.");

        var structAttr = structType.GetCustomAttribute<VespaStructAttribute>()!;
        var structName = structAttr.Name ?? VespaNaming.ToSnakeCase(structType.Name);

        var members = new List<(string Name, string VespaType)>();
        foreach (var prop in structType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var fieldAttr = prop.GetCustomAttribute<VespaFieldAttribute>();
            var memberName = fieldAttr?.Name ?? VespaNaming.ToSnakeCase(prop.Name);
            var memberContext = $"property '{prop.Name}' on struct '{structType.Name}'";
            var memberType = fieldAttr is { FieldType: not VespaFieldType.None }
                ? ToVespaTypeName(fieldAttr.FieldType)
                : ResolveClrType(prop.PropertyType, memberContext).VespaType;
            members.Add((memberName, memberType));
        }

        _structsInProgress.Remove(structType);

        // Nested structs were collected during member resolution, so dependencies
        // land in _structs before their containing struct.
        var model = new SchemaStructModel(structName, members);
        _structIndex[structType] = model;
        _structs.Add(model);
        return model;
    }

    // ── Rank profiles ─────────────────────────────────────────────────────────

    private static List<RankProfileModel> BuildRankProfiles(
        Type type,
        List<(string QueryName, string TensorType)> tensorInputs)
    {
        var profiles = new List<RankProfileModel>();

        foreach (var rp in type.GetCustomAttributes<VespaRankProfileAttribute>())
        {
            var profile = new RankProfileModel
            {
                Name = rp.Name,
                Inherits = rp.Inherits,
                FirstPhase = rp.FirstPhase,
                SecondPhase = rp.SecondPhase,
                SecondPhaseRerankCount = rp.SecondPhaseRerankCount,
                GlobalPhase = rp.GlobalPhase,
                GlobalPhaseRerankCount = rp.GlobalPhaseRerankCount,
                MatchFeatures = rp.MatchFeatures,
                SummaryFeatures = rp.SummaryFeatures,
                DiversityAttribute = rp.DiversityAttribute,
                DiversityMinGroups = rp.DiversityMinGroups
            };
#pragma warning disable CS0618 // Functions is obsolete but still honored
            profile.Functions.AddRange(ParseFunctionsDsl(rp.Functions, rp.Name));
#pragma warning restore CS0618
            profiles.Add(profile);
        }

        // Auto-generate a "default" profile carrying the tensor query inputs, unless
        // the user declared their own "default" (two same-name profiles fail deployment).
        if (tensorInputs.Count > 0 && !profiles.Any(p => p.Name == "default"))
            profiles.Insert(0, new RankProfileModel { Name = "default" });

        // Emit the tensor inputs into every profile that does not (transitively)
        // inherit them from another generated profile — a profile inheriting only
        // built-in/external profiles would otherwise get untyped query tensors.
        var names = profiles.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var profile in profiles)
            profile.EmitInputs = tensorInputs.Count > 0
                && (profile.Inherits is null || !names.Contains(profile.Inherits));

        foreach (var fn in type.GetCustomAttributes<VespaRankFunctionAttribute>())
            ResolveFunctionTarget(fn, profiles, type).Functions.Add(new RankFunctionModel(fn.Signature, fn.Expression));

        return profiles;
    }

    private static RankProfileModel ResolveFunctionTarget(
        VespaRankFunctionAttribute fn,
        List<RankProfileModel> profiles,
        Type type)
    {
        if (fn.Profile is not null)
            return profiles.FirstOrDefault(p => p.Name == fn.Profile)
                ?? throw new InvalidOperationException(
                    $"[VespaRankFunction(\"{fn.Signature}\")] on type '{type.Name}' targets rank profile " +
                    $"'{fn.Profile}', which is not declared (and not auto-generated) on this type.");

        return profiles.Count switch
        {
            1 => profiles[0],
            0 => throw new InvalidOperationException(
                $"[VespaRankFunction(\"{fn.Signature}\")] on type '{type.Name}' requires a rank profile; " +
                "declare one with [VespaRankProfile]."),
            _ => throw new InvalidOperationException(
                $"[VespaRankFunction(\"{fn.Signature}\")] on type '{type.Name}' is ambiguous: " +
                $"{profiles.Count} rank profiles exist ({string.Join(", ", profiles.Select(p => p.Name))}). " +
                "Set Profile to pick one.")
        };
    }

    private static IEnumerable<RankFunctionModel> ParseFunctionsDsl(string? functions, string profileName)
    {
        if (functions is null)
            yield break;

        foreach (var entry in functions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colonIdx = entry.IndexOf(':');
            if (colonIdx <= 0 || colonIdx == entry.Length - 1)
                throw new InvalidOperationException(
                    $"Invalid Functions entry '{entry}' in rank profile '{profileName}': " +
                    "expected 'signature: expression'. Prefer [VespaRankFunction(signature, expression)], " +
                    "which also supports expressions containing ':' or ';'.");

            yield return new RankFunctionModel(entry[..colonIdx].Trim(), entry[(colonIdx + 1)..].Trim());
        }
    }

    // ── Name tables ───────────────────────────────────────────────────────────

    private static string BuildIndexingString(IndexingMode mode)
    {
        var parts = new List<string>();
        if ((mode & IndexingMode.Index) != 0) parts.Add("index");
        if ((mode & IndexingMode.Attribute) != 0) parts.Add("attribute");
        if ((mode & IndexingMode.Summary) != 0) parts.Add("summary");
        return string.Join(" | ", parts);
    }

    private static string ToVespaTypeName(VespaFieldType ft) => ft switch
    {
        VespaFieldType.Bool => "bool",
        VespaFieldType.Byte => "byte",
        VespaFieldType.Int => "int",
        VespaFieldType.Long => "long",
        VespaFieldType.Float => "float",
        VespaFieldType.Double => "double",
        VespaFieldType.String => "string",
        VespaFieldType.Raw => "raw",
        VespaFieldType.Uri => "uri",
        VespaFieldType.Predicate => "predicate",
        VespaFieldType.ArrayBool => "array<bool>",
        VespaFieldType.ArrayByte => "array<byte>",
        VespaFieldType.ArrayInt => "array<int>",
        VespaFieldType.ArrayLong => "array<long>",
        VespaFieldType.ArrayFloat => "array<float>",
        VespaFieldType.ArrayDouble => "array<double>",
        VespaFieldType.ArrayString => "array<string>",
        VespaFieldType.WeightedSetString => "weightedset<string>",
        VespaFieldType.WeightedSetInt => "weightedset<int>",
        VespaFieldType.MapStringString => "map<string,string>",
        VespaFieldType.MapStringInt => "map<string,int>",
        _ => throw new NotSupportedException($"VespaFieldType.{ft} is not supported for schema generation.")
    };

    private static string? ToVespaMetricName(DistanceMetric m) => m switch
    {
        DistanceMetric.None => null,
        DistanceMetric.Euclidean => "euclidean",
        DistanceMetric.Angular => "angular",
        DistanceMetric.DotProduct => "dotproduct",
        DistanceMetric.Hamming => "hamming",
        DistanceMetric.Geodesic => "geodegrees",
#pragma warning disable CS0618 // mapped to its successor for backward compatibility
        DistanceMetric.InnerProduct => "prenormalized-angular",
#pragma warning restore CS0618
        DistanceMetric.PrenormalizedAngular => "prenormalized-angular",
        _ => null
    };

    private static string? ToVespaStemmingName(StemmingMode s) => s switch
    {
        StemmingMode.None => null,
        StemmingMode.Best => "best",
        StemmingMode.Multiple => "multiple",
        StemmingMode.Off => "none",
        StemmingMode.Shortest => "shortest",
        _ => null
    };

    private static string? ToVespaMatchName(MatchMode m) => m switch
    {
        MatchMode.None => null,
        MatchMode.Text => "text",
        MatchMode.Word => "word",
        MatchMode.Exact => "exact",
        MatchMode.Prefix => "prefix",
        MatchMode.Substring => "substring",
        MatchMode.Suffix => "suffix",
        MatchMode.Cased => "cased",
        MatchMode.Uncased => "uncased",
        MatchMode.Gram => "gram",
        _ => null
    };
}
