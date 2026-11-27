using System.Collections;
using System.IO.Compression;
using System.Xml.Linq;
using Vespa.Documents;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class ApplicationPackageSelectionTests
{
    [Fact]
    public void TypedSelections_FreezeValuesAndReturnNewOptionsForEachTarget()
    {
        var cutoff = 42L;
        var original = new ApplicationPackageOptions { NodeCount = 2, Redundancy = 2 };
        var first = original.WithDocumentSelection<Product>(p => p.Timestamp > cutoff);
        cutoff = 100;
        var second = first.WithDocumentSelection<Other>(p => p.Enabled);

        Assert.Null(original.DocumentSelections);
        Assert.Single(first.DocumentSelections!);
        Assert.Equal("(product.created_at > 42)", first.DocumentSelections!["product"]);
        Assert.Equal(2, second.DocumentSelections!.Count);
        Assert.Equal(2, second.NodeCount);
        Assert.Equal(2, second.Redundancy);

        using var output = new MemoryStream();
        VespaSchemaBuilder.GenerateApplicationPackage(output, [typeof(Product), typeof(Other)], second);
        var services = ReadServices(output);
        Assert.Equal("true", services.Descendants("documents").Single().Attribute("garbage-collection")?.Value);
        Assert.Equal("(product.created_at > 42)", Selection(services, "product"));
        Assert.Equal("(other.enabled == true)", Selection(services, "other"));
        Assert.All(services.Descendants("document"), document => Assert.Equal("store-only", document.Attribute("mode")?.Value));
        using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        using var schema = new StreamReader(zip.GetEntry("schemas/product.sd")!.Open());
        Assert.Equal(VespaSchemaBuilder.GenerateSchema<Product>(), schema.ReadToEnd());
        Assert.NotNull(zip.GetEntry("hosts.xml"));
    }

    [Fact]
    public void RawSelections_AreOrdinalSnapshotsAcrossAssignmentAndWithCopies()
    {
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["product"] = "product.created_at > now() - 86400" };
        var options = new ApplicationPackageOptions { DocumentSelections = source };
        var clone = options with { Redundancy = 1 };
        source["product"] = "false";
        source["other"] = "false";
        Assert.Single(options.DocumentSelections!);
        Assert.False(options.DocumentSelections!.ContainsKey("Product"));
        Assert.Same(options.DocumentSelections, clone.DocumentSelections);
        Assert.Equal("product.created_at > now() - 86400", Selection(Generate(options), "product"));

        var replacement = new Dictionary<string, string> { ["product"] = "true" };
        var replaced = clone with { DocumentSelections = replacement };
        replacement["product"] = "false";
        Assert.Equal("true", replaced.DocumentSelections!["product"]);
        Assert.Equal("product.created_at > now() - 86400", options.DocumentSelections["product"]);
    }

    [Fact]
    public void RawSelections_RejectDuplicateOrdinalTargetsInReferenceEqualityDictionary()
    {
        var first = new string("product".ToCharArray());
        var second = new string("product".ToCharArray());
        var source = new Dictionary<string, string>(ReferenceEqualityComparer.Instance) { [first] = "true", [second] = "false" };
        Assert.Equal(2, source.Count);
        Assert.Throws<ArgumentException>(() => new ApplicationPackageOptions { DocumentSelections = source });
    }

    [Fact]
    public void TypedSelections_RejectDuplicateTargetsIncludingRawEntries()
    {
        var options = new ApplicationPackageOptions().WithDocumentSelection<Product>(p => true);
        Assert.Throws<ArgumentException>(() => options.WithDocumentSelection<Product>(p => true));
        Assert.Throws<ArgumentException>(() => options.WithDocumentSelection<SameTarget>(p => false));
        var raw = new ApplicationPackageOptions { DocumentSelections = new Dictionary<string, string> { ["product"] = "true" } };
        Assert.Throws<ArgumentException>(() => raw.WithDocumentSelection<Product>(p => false));
        Assert.Equal("true", raw.DocumentSelections!["product"]);
    }

    [Fact]
    public void TypedSelections_ValidateInputsWithoutCallingUserCode()
    {
        var options = new ApplicationPackageOptions();
        var probe = new Probe();
        Assert.Throws<ArgumentNullException>(() => ApplicationPackageOptionsExtensions.WithDocumentSelection<Product>(null!, p => true));
        Assert.Throws<ArgumentNullException>(() => options.WithDocumentSelection<Product>(null!));
        Assert.Throws<InvalidOperationException>(() => options.WithDocumentSelection<Probe>(p => true));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.Label == probe.Value));
        Assert.Equal(0, probe.Calls);
        Assert.Null(options.DocumentSelections);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<services version=\"1.0\"/>")]
    public void CustomServicesXml_RejectsRuntimeSelectionsInEitherConfigurationOrder(string xml)
    {
        var custom = new ApplicationPackageOptions { CustomServicesXml = xml };
        Assert.Throws<ArgumentException>(() => custom.WithDocumentSelection<Product>(p => true));
        var options = new ApplicationPackageOptions().WithDocumentSelection<Product>(p => true) with { CustomServicesXml = xml };
        AssertRejectedBeforeZip(options);
        using var output = new MemoryStream();
        VespaSchemaBuilder.GenerateApplicationPackage<Product>(output, custom with { DocumentSelections = new Dictionary<string, string>() });
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("services.xml")!.Open());
        Assert.Equal(xml, reader.ReadToEnd());
    }

    [Theory]
    [InlineData("unknown", "true")]
    [InlineData("Product", "true")]
    [InlineData("", "true")]
    [InlineData("product.price > 0", "true")]
    [InlineData("id", "true")]
    [InlineData("product", "")]
    [InlineData("product", " \t\n")]
    [InlineData("product", null)]
    [InlineData("product", "product.label == \"\0\"")]
    public void RawSelections_RejectInvalidConfigurationBeforeWritingZip(string target, string? selection)
    {
        var options = new ApplicationPackageOptions { DocumentSelections = new Dictionary<string, string> { [target] = selection! } };
        AssertRejectedBeforeZip(options);
    }

    [Theory]
    [InlineData(typeof(Selected), "selected")]
    [InlineData(typeof(EmptySelected), "empty_selected")]
    [InlineData(typeof(InheritedSelected), "selected")]
    public void AttributeSelection_AlwaysConflictsWithRuntimeSelection(Type type, string documentType)
    {
        var options = new ApplicationPackageOptions { DocumentSelections = new Dictionary<string, string> { [documentType] = "true" } };
        using var output = new MemoryStream();
        var exception = Assert.Throws<ArgumentException>(() => VespaSchemaBuilder.GenerateApplicationPackage(output, type, options));
        Assert.Contains("attribute selection", exception.Message);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void RuntimeSelection_LeavesOtherTypesAndTheirAttributeSelectionsIntact()
    {
        var options = new ApplicationPackageOptions().WithDocumentSelection<Product>(p => true);
        using var output = new MemoryStream();
        VespaSchemaBuilder.GenerateApplicationPackage(output, [typeof(Product), typeof(Other), typeof(Selected)], options);
        var xml = ReadServices(output);
        Assert.Equal("true", Selection(xml, "product"));
        Assert.Null(Selection(xml, "other"));
        Assert.Equal("selected.value > now() - 86400", Selection(xml, "selected"));
    }

    [Fact]
    public void RuntimeSelection_EnumeratesDocumentTypesOnce()
    {
        var types = new SinglePassTypes();
        using var output = new MemoryStream();
        VespaSchemaBuilder.GenerateApplicationPackage(output, types,
            new ApplicationPackageOptions().WithDocumentSelection<Product>(p => true));
        Assert.Equal(1, types.Enumerations);
        Assert.Equal(2, ReadServices(output).Descendants("document").Count());
    }

    [Fact]
    public void RuntimeSelection_RejectsAmbiguousTargetsBeforeWritingZip()
    {
        using var output = new MemoryStream();
        var options = new ApplicationPackageOptions().WithDocumentSelection<Product>(p => true);
        Assert.Throws<InvalidOperationException>(() => VespaSchemaBuilder.GenerateApplicationPackage(output, [typeof(Product), typeof(SameTarget)], options));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Strings_UseEscapesSharedByRoutingAndGarbageCollection()
    {
        var label = "catalog \"quoted\" \\path\t\n\r\f <&>";
        var options = new ApplicationPackageOptions().WithDocumentSelection<Product>(p => p.Label == label);
        const string expected = "(product.display_name == \"catalog \\\"quoted\\\" \\\\path\\t\\n\\r\\f <&>\")";
        Assert.Equal(expected, options.DocumentSelections!["product"]);
        Assert.Equal(expected, Selection(Generate(options), "product"));
    }

    [Theory]
    [InlineData("terminal\\")]
    [InlineData("\0")]
    [InlineData("\b")]
    [InlineData("\v")]
    [InlineData("\u001F")]
    [InlineData("\uFFFF")]
    [InlineData("\u007F")]
    [InlineData("café")]
    [InlineData("日本")]
    [InlineData("😀")]
    public void Strings_RejectLiteralsWithoutPortablePackageRepresentation(string label)
    {
        Assert.Throws<NotSupportedException>(() => new ApplicationPackageOptions().WithDocumentSelection<Product>(p => p.Label == label));
    }

    [Fact]
    public void Strings_RejectMalformedUnicodeWithoutReplacingCharacters()
    {
        var label = new string(['\uD800']);
        Assert.Throws<NotSupportedException>(() => new ApplicationPackageOptions().WithDocumentSelection<Product>(p => p.Label == label));
    }

    [Fact]
    public void RawStrings_PreserveAttributeWhitespaceAndXmlMetacharacters()
    {
        const string raw = "product.display_name == \"a\t\r\nb<&>\"";
        var options = new ApplicationPackageOptions { DocumentSelections = new Dictionary<string, string> { ["product"] = raw } };
        Assert.Equal(raw, Selection(Generate(options), "product"));
    }

    [Fact]
    public void IntegralComparisons_AllowSmallValuesAndNativeBoundedFields()
    {
        var options = new ApplicationPackageOptions();
        var large = long.MaxValue;
        Assert.Equal("(product.created_at > 42)", options.WithDocumentSelection<Product>(p => p.Timestamp > 42).DocumentSelections!["product"]);
        Assert.Equal("(product.quantity == 9223372036854775807)", options.WithDocumentSelection<Product>(p => p.Quantity == large).DocumentSelections!["product"]);
        Assert.Equal("(product.quantity == product.created_at)", options.WithDocumentSelection<Product>(p => p.Quantity == p.Timestamp).DocumentSelections!["product"]);
        Assert.Equal("(product.int_override == product.created_at)", options.WithDocumentSelection<Product>(p => p.IntOverride == p.Timestamp).DocumentSelections!["product"]);
        var value = 9007199254740991L;
        Assert.Equal("(product.created_at == 9007199254740991)", options.WithDocumentSelection<Product>(p => p.Timestamp == value).DocumentSelections!["product"]);
        Assert.Equal("(product.level == 2)", options.WithDocumentSelection<Product>(p => p.Level == Level.High).DocumentSelections!["product"]);
    }

    [Theory]
    [InlineData(9007199254740992L)]
    [InlineData(9007199254740993L)]
    [InlineData(-9007199254740992L)]
    [InlineData(-9007199254740993L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void IntegralComparisons_RejectAmbiguousLargeValues(long cutoff)
    {
        Assert.Throws<NotSupportedException>(() => new ApplicationPackageOptions().WithDocumentSelection<Product>(p => p.Timestamp >= cutoff));
    }

    [Fact]
    public void IntegralComparisons_RespectSchemaOverridesAndEnumBackingTypes()
    {
        var options = new ApplicationPackageOptions();
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.LongOverride == p.Timestamp));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.Unsigned == p.Timestamp));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.Timestamp == p.OtherTimestamp));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.DoubleStoredAsLong == p.OtherDoubleStoredAsLong));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => (double)p.LongOverride == p.DoubleStoredAsLong));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.DecimalStoredAsLong == 9007199254740993m));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.WideLevel == WideLevel.High));
        var level = WideLevel.High;
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.WideLevel == level));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => (double)p.Timestamp > 1));
    }

    [Fact]
    public void MinimumInt64Literal_IsRejectedBeforeVespaCanRewriteItToInvalidGcSyntax()
    {
        var minimum = long.MinValue;
        var decimalMinimum = (decimal)long.MinValue;
        var options = new ApplicationPackageOptions();
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.Quantity > minimum));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.Price > decimalMinimum));
        Assert.Equal("(product.quantity > 0x8000000000000000)", DocumentCondition.For<Product>(p => p.Quantity > minimum));
    }

    [Fact]
    public void FieldOrdering_RequiresNativeSelectionEvenForSmallFields()
    {
        var options = new ApplicationPackageOptions();
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.Quantity < p.Timestamp));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.Rating >= p.OtherRating));
        var raw = new ApplicationPackageOptions { DocumentSelections = new Dictionary<string, string> { ["product"] = "product.quantity < product.created_at" } };
        Assert.Equal("product.quantity < product.created_at", Selection(Generate(raw), "product"));
    }

    [Fact]
    public void FloatingComparisons_KeepNativeSyntaxAndPrecisionRules()
    {
        var options = new ApplicationPackageOptions();
        Assert.Equal("(product.rating > 1.0E+100)", options.WithDocumentSelection<Product>(p => p.Rating > 1e100).DocumentSelections!["product"]);
        Assert.Equal("(product.price > 12.5)", options.WithDocumentSelection<Product>(p => p.Price > 12.5m).DocumentSelections!["product"]);
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<Product>(p => p.Quantity == 1.0000000000000000000001m));
    }

    [Fact]
    public void ScalarNullChecks_PreserveUnknownFieldsForIdOnlyRouting()
    {
        var options = new ApplicationPackageOptions();
        const string isNull = "((product.display_name == null) or ((product.display_name == product.display_name) and (product.display_name != product.display_name)))";
        Assert.Equal(isNull, options.WithDocumentSelection<Product>(p => p.Label == null).DocumentSelections!["product"]);
        Assert.Equal(isNull, options.WithDocumentSelection<Product>(p => null == p.Label).DocumentSelections!["product"]);
        string? missing = null;
        Assert.Equal(isNull, options.WithDocumentSelection<Product>(p => p.Label == missing).DocumentSelections!["product"]);
        Assert.Equal("not (" + isNull + ")", options.WithDocumentSelection<Product>(p => p.Label != null).DocumentSelections!["product"]);
        var hasValue = options.WithDocumentSelection<Product>(p => p.Optional.HasValue).DocumentSelections!["product"];
        Assert.StartsWith("not (", hasValue);
        Assert.Contains("product.optional != product.optional", hasValue);
        Assert.Equal("not (" + hasValue + ")", options.WithDocumentSelection<Product>(p => !p.Optional.HasValue).DocumentSelections!["product"]);
    }

    [Fact]
    public void NonScalarNullChecks_RequireNativeSelections()
    {
        Assert.Throws<NotSupportedException>(() => new ApplicationPackageOptions().WithDocumentSelection<Product>(p => p.Tags != null));
        var raw = new ApplicationPackageOptions { DocumentSelections = new Dictionary<string, string> { ["product"] = "product.tags != null" } };
        Assert.Equal("product.tags != null", Selection(Generate(raw), "product"));
    }

    [Fact]
    public void PackageFields_RejectNativeKindsWithoutComparableScalarValues()
    {
        var options = new ApplicationPackageOptions();
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<SpecialFields>(p => p.Predicate == null));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<SpecialFields>(p => p.Predicate == "test"));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<SpecialFields>(p => p.Array == null));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<SpecialFields>(p => p.Tensor == 1));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<SpecialFields>(p => p.BooleanArray));
    }

    [Fact]
    public void PackageFields_RejectConflictingNativeScalarCategories()
    {
        var options = new ApplicationPackageOptions();
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<SpecialFields>(p => p.StringStoredAsInt == "17"));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<SpecialFields>(p => p.IntStoredAsString == 17));
        Assert.Throws<NotSupportedException>(() => options.WithDocumentSelection<SpecialFields>(p => p.BoolStoredAsInt));
        Assert.Equal("(special_fields.string_stored_as_int == \"17\")", DocumentCondition.For<SpecialFields>(p => p.StringStoredAsInt == "17"));
    }

    [Fact]
    public void PackageRendering_LeavesLegacyCapturedHasValueRejectionUnchanged()
    {
        int? captured = 1;
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Product>(p => captured.HasValue));
        Assert.Throws<NotSupportedException>(() => DocumentSelection.For<Product>(p => captured.HasValue));
    }

    [Fact]
    public void PackageFields_MustBeIncludedInTheGeneratedSchema()
    {
        Assert.Throws<NotSupportedException>(() => new ApplicationPackageOptions().WithDocumentSelection<Product>(p => p.DebugOnly));
        Assert.DoesNotContain("debug_only", VespaSchemaBuilder.GenerateSchema<Product>());
        Assert.Equal("(product.debug_only == true)", DocumentCondition.For<Product>(p => p.DebugOnly));
    }

    private static XDocument Generate(ApplicationPackageOptions options)
    {
        using var output = new MemoryStream();
        VespaSchemaBuilder.GenerateApplicationPackage<Product>(output, options);
        return ReadServices(output);
    }

    private static XDocument ReadServices(MemoryStream output)
    {
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
        using var reader = new StreamReader(zip.GetEntry("services.xml")!.Open());
        return XDocument.Parse(reader.ReadToEnd());
    }

    private static string? Selection(XDocument services, string target) => services.Descendants("document")
        .Single(document => document.Attribute("type")?.Value == target).Attribute("selection")?.Value;

    private static void AssertRejectedBeforeZip(ApplicationPackageOptions options)
    {
        using var output = new MemoryStream();
        Assert.ThrowsAny<ArgumentException>(() => VespaSchemaBuilder.GenerateApplicationPackage<Product>(output, options));
        Assert.Equal(0, output.Length);
        Assert.True(output.CanWrite);
    }

    private sealed class SinglePassTypes : IEnumerable<Type>
    {
        public int Enumerations { get; private set; }
        public IEnumerator<Type> GetEnumerator()
        {
            if (++Enumerations != 1)
                throw new InvalidOperationException("Types enumerated twice.");
            return ((IEnumerable<Type>)[typeof(Product), typeof(Other)]).GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Probe
    {
        public int Calls { get; private set; }
        public string Value { get { Calls++; return "side effect"; } }
    }

    private enum Level { High = 2 }
    private enum WideLevel : long { High = 9007199254740993L }

    [VespaDocument("special_fields")]
    private sealed class SpecialFields
    {
        [VespaField(FieldType = VespaFieldType.Predicate)] public string? Predicate { get; init; }
        [VespaField(FieldType = VespaFieldType.ArrayString)] public string? Array { get; init; }
        [VespaField(FieldType = VespaFieldType.ArrayBool)] public bool BooleanArray { get; init; }
        [VespaField(FieldType = VespaFieldType.Int)] public string? StringStoredAsInt { get; init; }
        [VespaField(FieldType = VespaFieldType.String)] public int IntStoredAsString { get; init; }
        [VespaField(FieldType = VespaFieldType.Int)] public bool BoolStoredAsInt { get; init; }
        [VespaTensor("tensor<float>(x[1])")] public double Tensor { get; init; }
    }

    [VespaDocument("product", Mode = DocumentMode.StoreOnly)]
    private sealed class Product
    {
        public bool DebugOnly => throw new InvalidOperationException("Getter must not run.");
        [VespaField(Name = "created_at")] public long Timestamp { get; init; }
        [VespaField(Name = "display_name")] public string? Label { get; init; }
        [VespaField] public int Quantity { get; init; }
        [VespaField] public long OtherTimestamp { get; init; }
        [VespaField] public int? Optional { get; init; }
        [VespaField] public uint Unsigned { get; init; }
        [VespaField(FieldType = VespaFieldType.Long)] public int LongOverride { get; init; }
        [VespaField(FieldType = VespaFieldType.Int)] public long IntOverride { get; init; }
        [VespaField(FieldType = VespaFieldType.Long)] public double DoubleStoredAsLong { get; init; }
        [VespaField(FieldType = VespaFieldType.Long)] public double OtherDoubleStoredAsLong { get; init; }
        [VespaField(FieldType = VespaFieldType.Long)] public decimal DecimalStoredAsLong { get; init; }
        [VespaField(FieldType = VespaFieldType.Int)] public Level Level { get; init; }
        [VespaField(FieldType = VespaFieldType.Long)] public WideLevel WideLevel { get; init; }
        [VespaField] public double Rating { get; init; }
        [VespaField] public double OtherRating { get; init; }
        [VespaField] public decimal Price { get; init; }
        [VespaField] public string[]? Tags { get; init; }
    }

    [VespaDocument("product")]
    private sealed class SameTarget;

    [VespaDocument("other", Mode = DocumentMode.StoreOnly)]
    private sealed class Other
    {
        [VespaField] public bool Enabled { get; init; }
    }

    [VespaDocument("selected", Selection = "selected.value > now() - 86400")]
    private class Selected
    {
        [VespaField] public long Value { get; init; }
    }

    [VespaDocument("empty_selected", Selection = "")]
    private sealed class EmptySelected;

    private sealed class InheritedSelected : Selected;
}
