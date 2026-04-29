using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Xunit;

namespace Vespa.Tests.Unit;

public class VespaExtraFieldsTests
{
    /// <summary>
    /// Mirrors JsonOpts (which is internal) so we can test
    /// the [VespaExtraFields] modifier from outside the assembly.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { UseVespaExtraFields }
        }
    };

    private static void UseVespaExtraFields(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return;

        foreach (var prop in typeInfo.Properties)
        {
            if (prop.AttributeProvider is null)
                continue;

            if (prop.AttributeProvider.GetCustomAttributes(typeof(VespaExtraFieldsAttribute), false).Length > 0)
                prop.IsExtensionData = true;
        }
    }

    // ── Deserialization ──────────────────────────────────────────────────────

    [Fact]
    public void Deserialize_UnmappedFields_CapturedInExtraFields()
    {
        var json = """{"popularity": 0.95, "frequency": 42, "nome_prodotto": "Scarpe", "prezzo": 99.90}""";

        var result = JsonSerializer.Deserialize<ProductFields>(json, JsonOpts)!;

        Assert.Equal(0.95, result.Popularity);
        Assert.Equal(42, result.Frequency);
        Assert.NotNull(result.TenantFields);
        Assert.Equal(2, result.TenantFields.Count);
        Assert.Equal("Scarpe", result.TenantFields["nome_prodotto"].GetString());
        Assert.Equal(99.90, result.TenantFields["prezzo"].GetDouble(), precision: 2);
    }

    [Fact]
    public void Deserialize_NoExtraFields_DictionaryIsNull()
    {
        var json = """{"popularity": 0.5, "frequency": 10}""";

        var result = JsonSerializer.Deserialize<ProductFields>(json, JsonOpts)!;

        Assert.Equal(0.5, result.Popularity);
        Assert.Equal(10, result.Frequency);
        Assert.Null(result.TenantFields);
    }

    [Fact]
    public void Deserialize_OnlyExtraFields_MappedPropertiesDefault()
    {
        var json = """{"nome_prodotto": "Test", "categoria": "shoes"}""";

        var result = JsonSerializer.Deserialize<ProductFields>(json, JsonOpts)!;

        Assert.Equal(0.0, result.Popularity);
        Assert.Equal(0, result.Frequency);
        Assert.NotNull(result.TenantFields);
        Assert.Equal(2, result.TenantFields.Count);
    }

    // ── Serialization ────────────────────────────────────────────────────────

    [Fact]
    public void Serialize_ExtraFields_EmittedFlat()
    {
        var doc = new ProductFields
        {
            Popularity = 0.8,
            Frequency = 5,
            TenantFields = new Dictionary<string, JsonElement>
            {
                ["nome_prodotto"] = JsonDocument.Parse("\"Scarpe\"").RootElement,
                ["prezzo"] = JsonDocument.Parse("99.90").RootElement
            }
        };

        var json = JsonSerializer.Serialize(doc, JsonOpts);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        // Mapped fields present
        Assert.Equal(0.8, root.GetProperty("popularity").GetDouble());
        Assert.Equal(5, root.GetProperty("frequency").GetInt32());

        // Extra fields emitted flat (not nested under "tenant_fields")
        Assert.Equal("Scarpe", root.GetProperty("nome_prodotto").GetString());
        Assert.Equal(99.90, root.GetProperty("prezzo").GetDouble(), precision: 2);

        // No "tenant_fields" key in output
        Assert.False(root.TryGetProperty("tenant_fields", out _));
    }

    [Fact]
    public void Serialize_NullExtraFields_OnlyMappedFields()
    {
        var doc = new ProductFields { Popularity = 1.0, Frequency = 100 };

        var json = JsonSerializer.Serialize(doc, JsonOpts);
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;

        Assert.Equal(2, CountProperties(root));
        Assert.Equal(1.0, root.GetProperty("popularity").GetDouble());
        Assert.Equal(100, root.GetProperty("frequency").GetInt32());
    }

    // ── Round-trip ───────────────────────────────────────────────────────────

    [Fact]
    public void RoundTrip_ExtraFieldsPreserved()
    {
        var json = """{"popularity": 0.7, "frequency": 3, "custom_tag": "vip", "score": 42}""";

        var deserialized = JsonSerializer.Deserialize<ProductFields>(json, JsonOpts)!;
        var reserialized = JsonSerializer.Serialize(deserialized, JsonOpts);
        using var parsed = JsonDocument.Parse(reserialized);
        var root = parsed.RootElement;

        Assert.Equal(0.7, root.GetProperty("popularity").GetDouble());
        Assert.Equal(3, root.GetProperty("frequency").GetInt32());
        Assert.Equal("vip", root.GetProperty("custom_tag").GetString());
        Assert.Equal(42, root.GetProperty("score").GetInt32());
    }

    // ── Schema builder ───────────────────────────────────────────────────────

    [Fact]
    public void SchemaBuilder_ExtraFieldsProperty_NotEmittedInSchema()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<ExtraFieldsSchemaDoc>();

        Assert.Contains("field popularity type double", schema);
        Assert.Contains("field frequency type int", schema);
        Assert.DoesNotContain("tenant_fields", schema);
        Assert.DoesNotContain("field tenant", schema);
        Assert.DoesNotContain("TenantFields", schema);
    }

    // ── Complex extra field values ───────────────────────────────────────────

    [Fact]
    public void Deserialize_NestedExtraFields_PreservedAsJsonElement()
    {
        var json = """{"popularity": 0.5, "frequency": 1, "metadata": {"color": "red", "sizes": [38, 39, 40]}}""";

        var result = JsonSerializer.Deserialize<ProductFields>(json, JsonOpts)!;

        Assert.NotNull(result.TenantFields);
        var metadata = result.TenantFields["metadata"];
        Assert.Equal(JsonValueKind.Object, metadata.ValueKind);
        Assert.Equal("red", metadata.GetProperty("color").GetString());
    }

    // ── Helpers & test types ─────────────────────────────────────────────────

    private static int CountProperties(JsonElement obj)
    {
        var count = 0;
        foreach (var _ in obj.EnumerateObject())
            count++;
        return count;
    }
}

file record ProductFields
{
    public double Popularity { get; init; }
    public int Frequency { get; init; }

    [VespaExtraFields]
    public Dictionary<string, JsonElement>? TenantFields { get; init; }
}

[VespaDocument("extra_doc")]
file record ExtraFieldsSchemaDoc
{
    [VespaField(IndexingMode = IndexingMode.AttributeSummary)]
    public double Popularity { get; init; }

    [VespaField(IndexingMode = IndexingMode.AttributeSummary)]
    public int Frequency { get; init; }

    [VespaExtraFields]
    public Dictionary<string, JsonElement>? TenantFields { get; init; }
}
