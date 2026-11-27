using System.Text.Json.Serialization;

namespace Vespa.VectorData;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<bool>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(List<long>))]
[JsonSerializable(typeof(List<float>))]
[JsonSerializable(typeof(List<double>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(bool[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(long[]))]
[JsonSerializable(typeof(float[]))]
[JsonSerializable(typeof(double[]))]
internal sealed partial class VectorDataJsonContext : JsonSerializerContext;
