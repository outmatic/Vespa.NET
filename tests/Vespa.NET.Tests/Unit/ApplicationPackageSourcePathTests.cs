using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Xml.Linq;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class ApplicationPackageSourcePathTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory();
    private readonly ModuleBuilder _module = AssemblyBuilder
        .DefineDynamicAssembly(new AssemblyName("SourcePathModels"), AssemblyBuilderAccess.RunAndCollect)
        .DefineDynamicModule("Models");

    public void Dispose() => _directory.Delete(recursive: true);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void SourcePath_WritesAttributedFilesAlongsideSchemaAndServices(bool onnx, bool constant)
    {
        byte[] modelBytes = [0, 128, 255, 11];
        byte[] weightBytes = [42, 10, 0, 127];
        List<CustomAttributeBuilder> sources = [];
        if (onnx)
            sources.Add(Onnx("models/score.onnx", Source("score.bin", modelBytes)));
        if (constant)
            sources.Add(Constant("constants/weights.json", Source("weights.bin", weightBytes)));
        var type = Model("catalog", null, [.. sources]);
        using var output = new MemoryStream();

        VespaSchemaBuilder.GenerateApplicationPackage(output, type);

        Assert.True(output.CanWrite);
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        if (onnx)
            Assert.Equal(modelBytes, Read(zip, "models/score.onnx"));
        if (constant)
            Assert.Equal(weightBytes, Read(zip, "constants/weights.json"));
        Assert.Equal(2 + sources.Count, zip.Entries.Count);
        Assert.Equal(Encoding.UTF8.GetBytes(VespaSchemaBuilder.GenerateSchema(type)), Read(zip, "schemas/catalog.sd"));
        var services = XDocument.Parse(Encoding.UTF8.GetString(Read(zip, "services.xml")));
        var document = Assert.Single(services.Descendants("document"));
        Assert.Equal("catalog", (string?)document.Attribute("type"));
        Assert.Equal("store-only", (string?)document.Attribute("mode"));
        Assert.Equal("true", (string?)document.Attribute("global"));
        Assert.Equal("id.user > 0", (string?)document.Attribute("selection"));
        Assert.Equal("true", (string?)document.Parent!.Attribute("garbage-collection"));
    }

    [Fact]
    public void SourcePath_InheritedModelWinsOverConstantLaterDocumentAndAdditionalFile()
    {
        const string shared = "files/shared.bin";
        byte[] modelBytes = [1, 2, 3];
        byte[] otherBytes = [4, 5, 6];
        var parent = Model("parent", null, Onnx(shared, Source("model", modelBytes)));
        var competing = Source("competing", otherBytes);
        var first = Model("first", parent, Constant(shared, competing));
        var later = Model("later", null, Constant(shared, competing));
        var options = new ApplicationPackageOptions { AdditionalFiles = new Dictionary<string, string> { [shared] = competing } };
        var enumerated = false;
        IEnumerable<Type> DocumentTypes()
        {
            Assert.False(enumerated, "Document types must be enumerated only once.");
            enumerated = true;
            yield return first;
            yield return later;
        }

        using var output = new MemoryStream();
        VespaSchemaBuilder.GenerateApplicationPackage(output, DocumentTypes(), options);
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        Assert.Equal(modelBytes, Read(zip, shared));
        Assert.Equal(Encoding.UTF8.GetBytes(VespaSchemaBuilder.GenerateSchema(first)), Read(zip, "schemas/first.sd"));
        Assert.Equal(Encoding.UTF8.GetBytes(VespaSchemaBuilder.GenerateSchema(later)), Read(zip, "schemas/later.sd"));
        Assert.Equal(4, zip.Entries.Count);
    }

    [Fact]
    public void SourcePath_MissingFileReportsItsOriginalPath()
    {
        var missing = Path.Combine(_directory.FullName, "missing.onnx");
        var type = Model("catalog", null, Onnx("models/missing.onnx", missing));
        using var output = new MemoryStream();
        var error = Assert.Throws<FileNotFoundException>(() => VespaSchemaBuilder.GenerateApplicationPackage(output, type));
        Assert.Equal(missing, error.FileName);
    }

    [Theory]
    [InlineData("services.xml", false)]
    [InlineData("hosts.xml", true)]
    [InlineData("schemas/first.sd", false)]
    [InlineData("schemas/later.sd", true)]
    public void GeneratedPathCollision_RejectsAutomaticAssets(string destination, bool constant)
    {
        var file = Source("asset", [1]);
        var first = Model("first", null, constant ? Constant(destination, file) : Onnx(destination, file));
        var later = Model("later", null);
        using var output = new MemoryStream();
        var error = Assert.Throws<InvalidOperationException>(() =>
            VespaSchemaBuilder.GenerateApplicationPackage(output, [first, later], new() { NodeCount = 2 }));
        Assert.Contains(destination, error.Message);
    }

    private string Source(string name, byte[] bytes)
    {
        var path = Path.Combine(_directory.FullName, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Read(ZipArchive zip, string path)
    {
        using var entry = Assert.Single(zip.Entries, item => item.FullName == path).Open();
        using var buffer = new MemoryStream();
        entry.CopyTo(buffer);
        return buffer.ToArray();
    }

    // Emit the attributes on actual model types so SourcePath points into this test's private directory.
    private Type Model(string name, Type? parent, params CustomAttributeBuilder[] sources)
    {
        var type = _module.DefineType(name, TypeAttributes.Public, parent ?? typeof(object));
        var document = typeof(VespaDocumentAttribute);
        type.SetCustomAttribute(new CustomAttributeBuilder(document.GetConstructor([typeof(string)])!, [name],
            [document.GetProperty(nameof(VespaDocumentAttribute.Global))!,
             document.GetProperty(nameof(VespaDocumentAttribute.Mode))!,
             document.GetProperty(nameof(VespaDocumentAttribute.Selection))!],
            [true, DocumentMode.StoreOnly, "id.user > 0"]));
        foreach (var source in sources)
            type.SetCustomAttribute(source);
        return type.CreateType()!;
    }

    private static CustomAttributeBuilder Onnx(string destination, string source)
    {
        var attr = typeof(VespaOnnxModelAttribute);
        return new(attr.GetConstructor([typeof(string), typeof(string)])!, ["score", destination],
            [attr.GetProperty(nameof(VespaOnnxModelAttribute.SourcePath))!], [source]);
    }

    private static CustomAttributeBuilder Constant(string destination, string source)
    {
        var attr = typeof(VespaConstantAttribute);
        return new(attr.GetConstructor([typeof(string), typeof(string), typeof(string)])!, ["weights", destination, "tensor<float>(x[1])"],
            [attr.GetProperty(nameof(VespaConstantAttribute.SourcePath))!], [source]);
    }
}
