using System.Text.Json;
using System.Text.Json.Serialization;
using Vespa.Models.Attributes;
using Vespa.Models.Tensors;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class TensorTypeContextTests
{
    [Theory]
    [InlineData("tensor<float>(x[2])", "3F800000C0000000")]
    [InlineData("tensor<double>(x[2])", "3FF0000000000000C000000000000000")]
    [InlineData("tensor<int8>(x[2])", "01FE")]
    [InlineData("tensor<bfloat16>(x[2])", "3F80C000")]
    public void HexValue_ExplicitType_DecodesCorrectCellWidth(string type, string hex)
    {
        var options = new JsonSerializerOptions { Converters = { new VespaTensorConverter(type) } };

        var tensor = JsonSerializer.Deserialize<VespaTensor>($"\"{hex}\"", options);

        Assert.NotNull(tensor);
        Assert.Equal(type, tensor.Type);
        Assert.Equal(new double[] { 1, -2 }, tensor.GetDenseValues<double>());
    }

    [Theory]
    [InlineData("\"3F80000040000000\"")]
    [InlineData("""{"values":"3F80000040000000"}""")]
    [InlineData("""{"blocks":[{"address":{"tag":"a"},"values":"3F80000040000000"}]}""")]
    public void HexValues_WithoutType_RejectsAmbiguousData(string json)
    {
        var options = new JsonSerializerOptions { Converters = { new VespaTensorConverter() } };

        var error = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<VespaTensor>(json, options));

        Assert.Contains("tensor type", error.Message);
    }

    [Fact]
    public void HexValue_ModelAttributes_ProvideIndependentTypesForEachProperty()
    {
        var model = JsonSerializer.Deserialize<TypedTensors>("""
            {"float_vector":"3F800000C0000000","double_vector":"3FF0000000000000C000000000000000",
             "int8_vector":"01FE","bfloat16_vector":"3F80C000"}
            """, VespaJsonOptions.Default)!;

        Assert.Equal(new float[] { 1, -2 }, model.FloatVector!.GetDenseValues<float>());
        Assert.Equal(new double[] { 1, -2 }, model.DoubleVector!.GetDenseValues<double>());
        Assert.Equal(new sbyte[] { 1, -2 }, model.Int8Vector!.GetDenseValues<sbyte>());
        Assert.Equal(new float[] { 1, -2 }, model.Bfloat16Vector!.GetDenseValues<float>());
    }

    [Theory]
    [InlineData("type")]
    [InlineData("values")]
    [InlineData("cells")]
    [InlineData("blocks")]
    public void ShortValue_MappedLabelsMayUseWrapperPropertyNames(string label)
    {
        var json = $$$"""{"mapped":{"{{{label}}}":1.5,"other":-2}}""";

        var model = JsonSerializer.Deserialize<TypedTensors>(json, VespaJsonOptions.Default)!;

        var values = model.Mapped!.GetMappedValues<float>()!;
        Assert.Equal(2, values.Count);
        Assert.Equal(1.5f, values[label]);
        Assert.Equal(-2f, values["other"]);
        var roundTrip = JsonSerializer.Deserialize<TypedTensors>(JsonSerializer.Serialize(model, VespaJsonOptions.Default), VespaJsonOptions.Default)!;
        Assert.Equal(values, roundTrip.Mapped!.GetMappedValues<float>());
    }

    [Theory]
    [InlineData("type")]
    [InlineData("values")]
    [InlineData("cells")]
    [InlineData("blocks")]
    public void ShortValue_MappedReservedLabels_AlsoWorkWithoutSchemaMetadata(string label)
    {
        var options = new JsonSerializerOptions { Converters = { new VespaTensorConverter() } };

        var tensor = JsonSerializer.Deserialize<VespaTensor>($$"""{"{{label}}":1.5}""", options)!;

        Assert.Equal(1.5, tensor.GetMappedValues<double>()![label]);
    }

    [Theory]
    [InlineData("type", false)]
    [InlineData("values", false)]
    [InlineData("cells", false)]
    [InlineData("blocks", false)]
    [InlineData("type", true)]
    [InlineData("values", true)]
    [InlineData("cells", true)]
    [InlineData("blocks", true)]
    public void ValueOnly_MixedLabelsUseDeclaredShape(string label, bool hex)
    {
        var values = hex ? "\"3F800000C0000000\"" : "[1,-2]";
        var json = $$$"""{"mixed":{"{{{label}}}":{{{values}}}}}""";

        var model = JsonSerializer.Deserialize<TypedTensors>(json, VespaJsonOptions.Default)!;

        Assert.Equal(new float[] { 1, -2 }, model.Mixed!.GetMixedSingleSparse<float>()![label]);
    }

    [Fact]
    public void SelfDescribingHex_UsesResponseTypeInsteadOfDeclaredType()
    {
        var model = JsonSerializer.Deserialize<TypedTensors>("""
            {"float_vector":{"type":"tensor<double>(x[2])","values":"3FF0000000000000C000000000000000"}}
            """, VespaJsonOptions.Default)!;

        Assert.Equal(new double[] { 1, -2 }, model.FloatVector!.GetDenseValues<double>());
        Assert.Equal("tensor<double>(x[2])", model.FloatVector.Type);
    }

    [Fact]
    public void DeclaredMixedTensor_StillReadsWrappedBlocks()
    {
        var model = JsonSerializer.Deserialize<TypedTensors>("""
            {"mixed":{"blocks":[{"address":{"tag":"a"},"values":[1,-2]}]}}
            """, VespaJsonOptions.Default)!;

        Assert.Equal(new float[] { 1, -2 }, Assert.Single(model.Mixed!.Blocks!).GetValues<float>());
    }

    [Fact]
    public void EmptyValueOnly_MappedAndMixedTensorsRetainTheirShape()
    {
        var model = JsonSerializer.Deserialize<TypedTensors>("""{"mapped":{},"mixed":{}}""", VespaJsonOptions.Default)!;

        Assert.Empty(model.Mapped!.GetMappedValues<float>()!);
        Assert.Empty(model.Mixed!.GetMixedSingleSparse<float>()!);
    }

    [Fact]
    public void ModelAttribute_DoesNotOverrideAnExplicitJsonConverter()
    {
        var model = JsonSerializer.Deserialize<CustomTensorModel>("""{"vector":[42]}""", VespaJsonOptions.Default)!;

        Assert.Equal(new double[] { 42 }, model.Vector!.GetDenseValues<double>());
        Assert.Equal("custom", model.Vector.Type);
    }

    private sealed record TypedTensors
    {
        [VespaTensor("tensor<float>(x[2])")]
        public VespaTensor? FloatVector { get; init; }
        [VespaTensor("tensor<double>(x[2])")]
        public VespaTensor? DoubleVector { get; init; }
        [VespaTensor("tensor<int8>(x[2])")]
        public VespaTensor? Int8Vector { get; init; }
        [VespaTensor("tensor<bfloat16>(x[2])")]
        public VespaTensor? Bfloat16Vector { get; init; }
        [VespaTensor("tensor<float>(tag{})")]
        public VespaTensor? Mapped { get; init; }
        [VespaTensor("tensor<float>(tag{},x[2])")]
        public VespaTensor? Mixed { get; init; }
    }

    private sealed record CustomTensorModel
    {
        [VespaTensor("tensor<float>(x[1])")]
        [JsonConverter(typeof(CustomTensorConverter))]
        public VespaTensor? Vector { get; init; }
    }

    private sealed class CustomTensorConverter : JsonConverter<VespaTensor>
    {
        public override VespaTensor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            VespaTensor.FromDenseValues(JsonSerializer.Deserialize<double[]>(ref reader, options)!, "custom");

        public override void Write(Utf8JsonWriter writer, VespaTensor value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.GetDenseValues<double>(), options);
    }
}
