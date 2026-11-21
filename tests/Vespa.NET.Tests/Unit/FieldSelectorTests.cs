using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Query;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class FieldSelectorTests
{
    [Fact]
    public void DirectProperties_KeepMappingInheritanceAndOrdinaryObjectConversions()
    {
        Assert.Equal("display_name", VespaDocumentMeta.FieldName<Model>(p => p.Name));
        Assert.Equal("unmapped_score", VespaDocumentMeta.FieldName<Model>(p => p.UnmappedScore));
        Assert.Equal("base_value", VespaDocumentMeta.FieldName<Model>(p => p.InheritedValue));
        Assert.Equal("optional_count", VespaDocumentMeta.FieldName<Model>(p => p.OptionalCount));
        Assert.Equal("display_name", VespaDocumentMeta.FieldName<Model>(p => (object)p.Name));
        Assert.Equal("unmapped_score", VespaDocumentMeta.FieldName<Model>(p => (object)p.UnmappedScore));
    }

    [Fact]
    public void NestedCapturedAndStaticMembers_AreRejected()
    {
        var captured = 3;
        var another = new Model();
        Assert.Throws<ArgumentException>(() => VespaDocumentMeta.FieldName<Model>(p => p.Address.City));
        Assert.Throws<ArgumentException>(() => VespaDocumentMeta.FieldName<Model>(p => captured));
        Assert.Throws<ArgumentException>(() => VespaDocumentMeta.FieldName<Model>(p => another.Name));
        Assert.Throws<ArgumentException>(() => VespaDocumentMeta.FieldName<Model>(p => Model.StaticValue));
        Assert.Throws<ArgumentException>(() => VespaDocumentMeta.FieldName<Model>(p => p.PublicField));
        Assert.Throws<ArgumentNullException>(() => VespaDocumentMeta.FieldName<Model>(null!));
    }

    [Fact]
    public void NumericCheckedAndOtherUnaryExpressions_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => VespaDocumentMeta.FieldName<Model>(p => (double)p.UnmappedScore));
        Assert.Throws<ArgumentException>(() => VespaDocumentMeta.FieldName<Model>(p => checked((byte)p.UnmappedScore)));
        Assert.Throws<ArgumentException>(() => VespaDocumentMeta.FieldName<Model>(p => -p.UnmappedScore));
        Assert.Throws<ArgumentException>(() => VespaDocumentMeta.FieldName<Model>(p => !p.Active));
        Assert.Throws<ArgumentException>(() => VespaDocumentMeta.FieldName<Model>(p => p[0]));
    }

    [Fact]
    public void APrivateGetter_IsNotAPublicReadableField()
    {
        var parameter = Expression.Parameter(typeof(Model), "p");
        var access = Expression.Property(parameter, nameof(Model.PrivateGetter));
        var selector = Expression.Lambda<Func<Model, object?>>(Expression.Convert(access, typeof(object)), parameter);
        Assert.Throws<ArgumentException>(() => VespaDocumentMeta.FieldName(selector));
    }

    [Fact]
    public void PropertyResolution_DoesNotInvokeGetters()
    {
        Assert.Equal("throws_on_read", VespaDocumentMeta.FieldName<Model>(p => p.ThrowsOnRead));
        Assert.Equal("(selector_model.throws_on_read > 0)", DocumentCondition.For<Model>(p => p.ThrowsOnRead > 0));
    }

    [Fact]
    public void QueryAliases_RemainQueryableWhenJsonIgnoresThem()
    {
        var query = YqlBuilder<Model>.Select(p => p.ExternalScore)
            .Where(w => w.Field(p => p.ExternalScore).GreaterThan(10))
            .OrderBy(p => p.ExternalScore)
            .Build();
        Assert.Contains("select external_score from selector_model", query);
        Assert.Contains("where external_score > 10", query);
        Assert.Contains("order by external_score", query);
        Assert.Equal("external_score", VespaDocumentMeta.FieldName<Model>(p => p.ExternalScore));
    }

    [Fact]
    public void DocumentPredicates_RejectIdentityAndUnboundProperties()
    {
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Model>(p => p.Id == "item"));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Model>(p => p.ExtraFields == null));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Model>(p => p.ExternalScore > 0));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Model>(p => p.IgnoredFlag));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Model>(p => p.IgnoredCount.HasValue));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<ExtensionModel>(p => p.ExtensionFields != null));
    }

    [Fact]
    public void ConditionalJsonIgnores_KeepTheirDocumentBinding()
    {
        Assert.Equal("(selector_model.optional_count != null)", DocumentCondition.For<Model>(p => p.OptionalCount != null));
        Assert.Equal("(selector_model.conditional_count == 7)", DocumentCondition.For<Model>(p => p.ConditionalCount == 7));
        Assert.Equal("(selector_model.explicitly_included == true)", DocumentCondition.For<Model>(p => p.ExplicitlyIncluded));
    }

#if NET10_0_OR_GREATER
    [Fact]
    public void DirectionalJsonIgnores_KeepTheirDocumentBinding()
    {
        Assert.Equal("(selector_model.reading_ignored == 1)", DocumentCondition.For<Model>(p => p.ReadingIgnored == 1));
        Assert.Equal("(selector_model.writing_ignored == 1)", DocumentCondition.For<Model>(p => p.WritingIgnored == 1));
        Assert.Equal(2, new TypedFieldUpdateBuilder<Model>()
            .Field(p => p.ReadingIgnored, FieldOp.Assign(1))
            .Field(p => p.WritingIgnored, FieldOp.Assign(1)).Build().Count);
    }
#endif

    [Fact]
    public void BasePropertyExpressions_UseTheModelsVirtualOverride()
    {
        var parameter = Expression.Parameter(typeof(Overrides), "p");
        var ignored = Expression.Property(parameter, typeof(VirtualBase).GetProperty(nameof(VirtualBase.Ignored))!);
        var ignoredSelector = Expression.Lambda<Func<Overrides, object?>>(ignored, parameter);
        Assert.Throws<NotSupportedException>(() => new TypedFieldUpdateBuilder<Overrides>().Field(ignoredSelector, FieldOp.Assign("value")));
        var predicate = Expression.Lambda<Func<Overrides, bool>>(
            Expression.Equal(ignored, Expression.Constant("value")), parameter);
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For(predicate));

        var renamed = Expression.Property(parameter, typeof(VirtualBase).GetProperty(nameof(VirtualBase.Renamed))!);
        var renamedSelector = Expression.Lambda<Func<Overrides, object?>>(renamed, parameter);
        var fields = new TypedFieldUpdateBuilder<Overrides>().Field(renamedSelector, FieldOp.Assign("value")).Build();
        Assert.True(fields.ContainsKey("derived_name"));
        Assert.Equal("(overrides.derived_name == \"value\")", DocumentCondition.For<Overrides>(p => p.Renamed == "value"));
    }

    [Fact]
    public void HiddenBaseProperties_AreRejectedAndInheritedInterfacePropertiesWork()
    {
        var parameter = Expression.Parameter(typeof(HidesBase), "p");
        var access = Expression.Property(parameter, typeof(HiddenBase).GetProperty(nameof(HiddenBase.Name))!);
        var selector = Expression.Lambda<Func<HidesBase, object?>>(access, parameter);
        Assert.Throws<NotSupportedException>(() => new TypedFieldUpdateBuilder<HidesBase>().Field(selector, FieldOp.Assign("value")));
        var fields = new TypedFieldUpdateBuilder<IInherited>().Field(p => p.Name, FieldOp.Assign("value")).Build();
        Assert.True(fields.ContainsKey("name"));
    }

    [Fact]
    public void HiddenInterfaceBaseProperties_AreRejected()
    {
        AssertHidden<IHidden>();
        AssertHidden<ILeaf>();
        AssertHidden<IIgnored>();
        Assert.True(new TypedFieldUpdateBuilder<IHidden>().Field(p => p.Name, FieldOp.Assign("value"))
            .Build().ContainsKey("child_name"));
        Assert.True(new TypedFieldUpdateBuilder<ILeaf>().Field(p => p.Name, FieldOp.Assign("value"))
            .Build().ContainsKey("child_name"));
        Assert.Throws<NotSupportedException>(() => new TypedFieldUpdateBuilder<IIgnored>().Field(p => p.Name, FieldOp.Assign("value")));

        static void AssertHidden<T>() where T : class
        {
            var parameter = Expression.Parameter(typeof(T), "p");
            var access = Expression.Property(parameter, typeof(IBase).GetProperty(nameof(IBase.Name))!);
            var selector = Expression.Lambda<Func<T, object?>>(access, parameter);
            Assert.Throws<NotSupportedException>(() => new TypedFieldUpdateBuilder<T>().Field(selector, FieldOp.Assign("value")));
        }
    }

    [Fact]
    public async Task AnUndecoratedOverride_CannotUseAnAliasAbsentFromItsPutBody()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new());
        handler.QueueResponse(new(HttpStatusCode.OK) { Content = new StringContent("{}") });
        await documents.PutAsync("item", new UndecoratedOverride { Renamed = "value" });
        using var body = JsonDocument.Parse(await Assert.Single(handler.Requests).Content!.ReadAsStringAsync());
        var fields = body.RootElement.GetProperty("fields");
        Assert.Equal("value", fields.GetProperty("renamed").GetString());
        Assert.False(fields.TryGetProperty("base_name", out _));

        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<UndecoratedOverride>(p => p.Renamed == "value"));
        await Assert.ThrowsAsync<NotSupportedException>(() => documents.UpdateFieldsAsync<UndecoratedOverride>("item",
            update => update.Field(p => p.Renamed, FieldOp.Assign("updated"))));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void JsonAliases_RequireMatchingVespaMetadataForDocumentBuilders()
    {
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<JsonAliases>(p => p.Name == "value"));
        Assert.Throws<NotSupportedException>(() => new TypedFieldUpdateBuilder<JsonAliases>().Field(p => p.Name, FieldOp.Assign("value")));
        Assert.Equal("(json_aliases.stored_name == \"value\")", DocumentCondition.For<JsonAliases>(p => p.StoredName == "value"));
        Assert.True(new TypedFieldUpdateBuilder<JsonAliases>().Field(p => p.StoredName, FieldOp.Assign("value"))
            .Build().ContainsKey("stored_name"));
    }

    [Fact]
    public void DocumentUpdateBuilder_RejectsMetadataButKeepsStoredProperties()
    {
        var update = new TypedFieldUpdateBuilder<Model>();
        Assert.Throws<NotSupportedException>(() => update.Field(p => p.Id, FieldOp.Assign("item")));
        Assert.Throws<NotSupportedException>(() => update.Field(p => p.ExtraFields, FieldOp.ClearField()));
        Assert.Throws<NotSupportedException>(() => update.Field(p => p.ExternalScore, FieldOp.Assign(5)));
        Assert.Throws<NotSupportedException>(() => new TypedFieldUpdateBuilder<ExtensionModel>()
            .Field(p => p.ExtensionFields, FieldOp.ClearField()));
        update.Field(p => p.Name, FieldOp.Assign("new"))
            .Field(p => p.OptionalCount, FieldOp.Assign(2))
            .Field(p => p.ConditionalCount, FieldOp.Assign(7))
            .Field(p => p.InheritedValue, FieldOp.Increment());
        Assert.Equal(["base_value", "conditional_count", "display_name", "optional_count"], update.Build().Keys.Order());
    }

    [Fact]
    public async Task InvalidSelectors_FailBeforeHttpAndConditionCallbacks()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new());
        var callbackInvoked = false;

        await Assert.ThrowsAsync<ArgumentException>(() => documents.UpdateFieldsAsync<Model>("item",
            update => update.Field(p => p.Address.City, FieldOp.Assign("wrong"))));
        await Assert.ThrowsAsync<NotSupportedException>(() => documents.UpdateFieldsAsync<Model>("item",
            update => update.Field(p => p.ExternalScore, FieldOp.Assign(5))));
        await Assert.ThrowsAsync<NotSupportedException>(() => documents.PutAsync("item", new Model(), condition: p => p.Id == "item"));
        await Assert.ThrowsAsync<NotSupportedException>(() => documents.UpdateFieldsAsync<Model>("item",
            _ => callbackInvoked = true, condition: p => p.IgnoredFlag));

        Assert.False(callbackInvoked);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ValidUpdate_SendsOnlyMappedDocumentFields()
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        IDocumentOperations documents = new DocumentOperations(http, new());
        handler.QueueResponse(new(HttpStatusCode.OK) { Content = new StringContent("{}") });
        await documents.UpdateFieldsAsync<Model>("item", update => update
            .Field(p => p.Name, FieldOp.Assign("updated"))
            .Field(p => p.InheritedValue, FieldOp.Increment()));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/document/v1/test/selector_model/docid/item", request.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        var fields = body.RootElement.GetProperty("fields");
        Assert.Equal("updated", fields.GetProperty("display_name").GetProperty("assign").GetString());
        Assert.Equal(1, fields.GetProperty("base_value").GetProperty("increment").GetInt32());
        Assert.Equal(2, fields.EnumerateObject().Count());
    }

    private class BaseModel
    {
        [VespaField(Name = "base_value")]
        public int InheritedValue { get; init; }
    }

    [VespaDocument("selector_model", Namespace = "test")]
    private sealed class Model : BaseModel
    {
        [VespaField(Name = "display_name")]
        public string Name { get; init; } = "";
        public int UnmappedScore { get; init; }
        public bool Active { get; init; }
        public Address Address { get; init; } = new();
        public int PublicField = 1;
        public static int StaticValue => 1;
        public int PrivateGetter { private get; set; }
        public int ThrowsOnRead => throw new InvalidOperationException("Do not invoke this getter.");
        public int this[int index] => index;

        [VespaId]
        public string Id { get; init; } = "";
        [VespaExtraFields]
        public Dictionary<string, JsonElement>? ExtraFields { get; init; }
        [VespaField(Name = "external_score"), JsonIgnore]
        public int ExternalScore { get; init; }
        [JsonIgnore]
        public bool IgnoredFlag => throw new InvalidOperationException("Do not invoke this getter.");
        [JsonIgnore]
        public int? IgnoredCount { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? OptionalCount { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int ConditionalCount { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public bool ExplicitlyIncluded { get; init; }
#if NET10_0_OR_GREATER
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenReading)]
        public int ReadingIgnored { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
        public int WritingIgnored { get; init; }
#endif
    }

    private sealed class Address { public string City { get; init; } = ""; }

    [VespaDocument("extension_model")]
    private sealed class ExtensionModel
    {
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionFields { get; init; }
    }

    private class VirtualBase
    {
        public virtual string Ignored { get; init; } = "";
        [VespaField(Name = "base_name")]
        public virtual string Renamed { get; init; } = "";
    }

    [VespaDocument("overrides")]
    private sealed class Overrides : VirtualBase
    {
        [JsonIgnore]
        public override string Ignored { get; init; } = "";
        [VespaField(Name = "derived_name")]
        public override string Renamed { get; init; } = "";
    }

    [VespaDocument("undecorated", Namespace = "test")]
    private sealed class UndecoratedOverride : VirtualBase
    {
        public override string Renamed { get; init; } = "";
    }

    [VespaDocument("json_aliases")]
    private sealed class JsonAliases
    {
        [JsonPropertyName("custom_name")]
        public string Name { get; init; } = "";
        [JsonPropertyName("other_name"), VespaField(Name = "stored_name")]
        public string StoredName { get; init; } = "";
    }

    private class HiddenBase { public string Name { get; init; } = ""; }
    private sealed class HidesBase : HiddenBase { public new string Name { get; init; } = ""; }
    private interface IBase { string Name { get; } }
    private interface IInherited : IBase;
    private interface IHidden : IBase
    {
        [VespaField(Name = "child_name")]
        new string Name { get; }
    }
    private interface ILeaf : IHidden;
    private interface IIgnored : IBase
    {
        [JsonIgnore]
        new string Name { get; }
    }
}
