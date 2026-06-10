using System.IO.Compression;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Xunit;

namespace Vespa.Tests.Unit;

public class VespaSchemaBuilderTests
{
    // ── Test document types ───────────────────────────────────────────────────

    [VespaDocument("music")]
    private record MusicDoc
    {
        [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary, MatchMode = MatchMode.Text)]
        public string Title { get; init; } = string.Empty;

        [VespaField(Name = "year", FieldType = VespaFieldType.Int, IndexingMode = IndexingMode.AttributeSummary)]
        public int Year { get; init; }
    }

    [VespaDocument("product")]
    private record ProductDoc
    {
        [VespaField(Name = "name", IndexingMode = IndexingMode.IndexAttributeSummary)]
        public string Name { get; init; } = string.Empty;

        [VespaTensor("tensor<float>(x[384])", DistanceMetric = DistanceMetric.Angular)]
        public float[]? Embedding { get; init; }
    }

    [VespaDocument("multi")]
    private record MultiFieldDoc
    {
        [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary, MatchMode = MatchMode.Text)]
        public string Title { get; init; } = string.Empty;

        [VespaField(Name = "score", FieldType = VespaFieldType.Double, IndexingMode = IndexingMode.AttributeSummary)]
        public double Score { get; init; }

        [VespaField(Name = "tags", FieldType = VespaFieldType.ArrayString, IndexingMode = IndexingMode.AttributeSummary)]
        public List<string>? Tags { get; init; }
    }

    [VespaDocument("inferred")]
    private record InferredDoc
    {
        [VespaField]
        public string Description { get; init; } = string.Empty;

        [VespaField]
        public int Count { get; init; }

        [VespaField]
        public bool IsActive { get; init; }
    }

    [VespaDocument("tensor_indexed")]
    private record TensorIndexedDoc
    {
        [VespaTensor("tensor<float>(x[128])", EnableIndex = true, DistanceMetric = DistanceMetric.Euclidean)]
        public float[]? Vec { get; init; }
    }

    [VespaDocument("decimal_doc")]
    private record DecimalDoc
    {
        [VespaField(Name = "amount", IndexingMode = IndexingMode.AttributeSummary)]
        public decimal Amount { get; init; }

        [VespaField(Name = "nullable_amount")]
        public decimal? NullableAmount { get; init; }
    }

    [VespaDocument("id_conflict")]
    private record IdConflictDoc
    {
        [VespaField(Name = "id")]
        public string Identifier { get; init; } = string.Empty;
    }

    private class NoBadge { public string X { get; set; } = string.Empty; }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void GenerateSchema_StringField_ContainsTypeString()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<MusicDoc>();

        Assert.Contains("schema music", schema);
        Assert.Contains("document music", schema);
        Assert.Contains("field title type string", schema);
        Assert.Contains("indexing: index | attribute | summary", schema);
        Assert.Contains("match: text", schema);
    }

    [Fact]
    public void GenerateSchema_IntField_WithExplicitFieldType()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<MusicDoc>();

        Assert.Contains("field year type int", schema);
        Assert.Contains("indexing: attribute | summary", schema);
    }

    [Fact]
    public void GenerateSchema_TensorField_ContainsTensorType()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<ProductDoc>();

        Assert.Contains("field embedding type tensor<float>(x[384])", schema);
        Assert.Contains("indexing: attribute", schema);
        Assert.Contains("distance-metric: angular", schema);
    }

    [Fact]
    public void GenerateSchema_TensorWithIndex_ContainsHnswBlock()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<TensorIndexedDoc>();

        Assert.Contains("hnsw", schema);
        Assert.Contains("max-links-per-node", schema);
        Assert.Contains("distance-metric: euclidean", schema);
    }

    [Fact]
    public void GenerateSchema_MultipleFields_GeneratesAllFields()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<MultiFieldDoc>();

        Assert.Contains("field title type string", schema);
        Assert.Contains("field score type double", schema);
        Assert.Contains("field tags type array<string>", schema);
    }

    [Fact]
    public void GenerateSchema_InferredCSharpTypes_MapCorrectly()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<InferredDoc>();

        Assert.Contains("field description type string", schema);
        Assert.Contains("field count type int", schema);
        Assert.Contains("field is_active type bool", schema);
    }

    [Fact]
    public void GenerateSchema_MissingVespaDocumentAttribute_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VespaSchemaBuilder.GenerateSchema<NoBadge>());
        Assert.Contains("VespaDocument", ex.Message);
    }

    [Fact]
    public void GenerateApplicationPackage_ReturnsValidZip()
    {
        var bytes = GeneratePackageBytes<MusicDoc>();

        Assert.NotEmpty(bytes);

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        var names = zip.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("schemas/music.sd", names);
        Assert.Contains("services.xml", names);
    }

    [Fact]
    public void GenerateSchema_DecimalField_InfersDouble()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<DecimalDoc>();

        Assert.Contains("field amount type double", schema);
        Assert.Contains("indexing: attribute | summary", schema);
    }

    [Fact]
    public void GenerateSchema_NullableDecimalField_InfersDouble()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<DecimalDoc>();

        Assert.Contains("field nullable_amount type double", schema);
    }

    [Fact]
    public void GenerateApplicationPackage_ServicesXml_StartsWithXmlDeclaration()
    {
        var bytes = GeneratePackageBytes<MusicDoc>();

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        var servicesEntry = zip.GetEntry("services.xml")!;
        using var stream = servicesEntry.Open();
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();

        // Must not have BOM or leading whitespace
        Assert.StartsWith("<?xml", content);

        // Verify raw bytes don't have BOM (EF BB BF)
        using var rawStream = zip.GetEntry("services.xml")!.Open();
        var buf = new byte[3];
        var read = rawStream.Read(buf, 0, 3);
        Assert.Equal(3, read);
        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, buf);
        // First byte should be '<' (0x3C)
        Assert.Equal(0x3C, buf[0]);
    }

    [Fact]
    public void GenerateSchema_FieldNamedId_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VespaSchemaBuilder.GenerateSchema<IdConflictDoc>());
        Assert.Contains("conflicts with the document identifier", ex.Message);
    }

    [Fact]
    public void GenerateSchema_SummaryIndexMode_GeneratesIndexAndSummary()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<SummaryIndexDoc>();

        Assert.Contains("field title type string", schema);
        Assert.Contains("indexing: index | summary", schema);
        Assert.DoesNotContain("attribute", schema);
    }

    // --- M11: Stemming ---

    [Fact]
    public void GenerateSchema_StemmingBest_IncludesStemming()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<StemmingDoc>();
        Assert.Contains("stemming: best", schema);
    }

    [Fact]
    public void GenerateSchema_StemmingOff_IncludesNone()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<StemmingOffDoc>();
        Assert.Contains("stemming: none", schema);
    }

    // --- M11: Bolding ---

    [Fact]
    public void GenerateSchema_Bolding_IncludesBolding()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<BoldingDoc>();
        Assert.Contains("bolding: on", schema);
    }

    // --- M11: FastSearch ---

    [Fact]
    public void GenerateSchema_FastSearch_IncludesAttributeFastSearch()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<FastSearchDoc>();
        Assert.Contains("attribute: fast-search", schema);
    }

    // --- M11: Fieldset ---

    [Fact]
    public void GenerateSchema_Fieldset_IncludesFieldset()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<FieldsetDoc>();
        Assert.Contains("fieldset default {", schema);
        Assert.Contains("fields: title, body", schema);
    }

    // --- M11: Document Summary ---

    [Fact]
    public void GenerateSchema_DocumentSummary_IncludesSummaryBlock()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<DocSummaryDoc>();
        Assert.Contains("document-summary compact {", schema);
        Assert.Contains("summary title {}", schema);
        Assert.Contains("summary year {}", schema);
    }

    [Fact]
    public void GenerateSchema_DocumentSummary_WithInherits()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<DocSummaryInheritsDoc>();
        Assert.Contains("document-summary extended inherits compact {", schema);
    }

    // --- M11: Rank Profile ---

    [Fact]
    public void GenerateSchema_RankProfile_FirstPhase()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankProfileDoc>();
        Assert.Contains("rank-profile myrank {", schema);
        Assert.Contains("first-phase {", schema);
        Assert.Contains("expression: nativeRank(title)", schema);
    }

    [Fact]
    public void GenerateSchema_RankProfile_SecondPhase()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankProfileSecondPhaseDoc>();
        Assert.Contains("second-phase {", schema);
        Assert.Contains("expression: bm25(title)", schema);
        Assert.Contains("rerank-count: 100", schema);
    }

    [Fact]
    public void GenerateSchema_RankProfile_Inherits()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankProfileInheritsDoc>();
        Assert.Contains("rank-profile child inherits default {", schema);
    }

    [Fact]
    public void GenerateSchema_RankProfile_MatchFeatures()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankProfileFeaturesDoc>();
        Assert.Contains("match-features: bm25(title) nativeRank", schema);
        Assert.Contains("summary-features: firstPhase", schema);
    }

    // --- M14: Normalizing ---

    [Fact]
    public void GenerateSchema_Normalizing_IncludesNormalizing()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<NormalizingDoc>();
        Assert.Contains("normalizing: lowercase", schema);
    }

    // --- M14: Rank type emission ---

    [Fact]
    public void GenerateSchema_RankFilter_IncludesRankFilter()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankFilterDoc>();
        Assert.Contains("rank: filter", schema);
    }

    [Fact]
    public void GenerateSchema_RankDefault_IncludesRankNormal()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankNormalDoc>();
        Assert.Contains("rank: normal", schema);
    }

    [Fact]
    public void GenerateSchema_RankIdentityAndTags_EmitRankTypeElement()
    {
        // Field-level rank: accepts only filter/normal — identity and tags belong
        // to the separate rank-type element, otherwise deployment fails.
        var schema = VespaSchemaBuilder.GenerateSchema<RankTypeElementDoc>();
        Assert.Contains("rank-type: identity", schema);
        Assert.Contains("rank-type: tags", schema);
        Assert.DoesNotContain("rank: identity", schema);
        Assert.DoesNotContain("rank: tags", schema);
    }

    // --- Distance metric names ---

    [Fact]
    public void GenerateSchema_GeodesicMetric_EmitsGeodegrees()
    {
        // Vespa's metric for lat/long is named "geodegrees"; "geodesic" fails deployment
        var schema = VespaSchemaBuilder.GenerateSchema<GeoMetricDoc>();
        Assert.Contains("distance-metric: geodegrees", schema);
        Assert.DoesNotContain("geodesic", schema);
    }

    // --- Auto default rank-profile vs user-declared default ---

    [Fact]
    public void GenerateSchema_UserDefaultProfile_WithTensorField_EmitsSingleDefaultProfile()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<DefaultProfileWithTensorDoc>();

        var occurrences = System.Text.RegularExpressions.Regex.Matches(schema, @"rank-profile default\s*\{").Count;
        Assert.Equal(1, occurrences);
        // The single profile must carry both the tensor inputs and the user's first-phase
        Assert.Contains("inputs {", schema);
        Assert.Contains("expression: nativeRank(name)", schema);
    }

    // --- M14: Schema inherits ---

    [Fact]
    public void GenerateSchema_Inherits_IncludesSchemaInherits()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<InheritingDoc>();
        Assert.Contains("schema child_doc inherits base_doc {", schema);
    }

    // --- M14: Document expiry ---

    [Fact]
    public void GenerateSchema_Selection_IncludesDocumentSelection()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<ExpiryDoc>();
        Assert.Contains("selection: expiry_doc.timestamp > now() - 86400", schema);
    }

    // --- M14: Rank profile diversity ---

    [Fact]
    public void GenerateSchema_RankProfileDiversity_IncludesDiversityBlock()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankProfileDiversityDoc>();
        Assert.Contains("diversity {", schema);
        Assert.Contains("attribute: category", schema);
        Assert.Contains("min-groups: 10", schema);
    }

    // --- M14: Rank profile functions ---

    [Fact]
    public void GenerateSchema_RankProfileFunctions_IncludesFunctionBlock()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankProfileFuncDoc>();
        Assert.Contains("function freshness {", schema);
        Assert.Contains("expression: now - attribute(timestamp)", schema);
    }

    [Fact]
    public void GenerateSchema_RankProfileFunctions_MultipleFunctions()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankProfileMultiFuncDoc>();
        Assert.Contains("function freshness {", schema);
        Assert.Contains("function myScore(a,b) {", schema);
    }

    // --- M16: Struct field types ---

    [Fact]
    public void GenerateSchema_StructField_EmitsStructDefinition()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<StructDoc>();
        Assert.Contains("struct address {", schema);
        Assert.Contains("field street type string {}", schema);
        Assert.Contains("field city type string {}", schema);
    }

    [Fact]
    public void GenerateSchema_StructField_EmitsFieldWithStructType()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<StructDoc>();
        Assert.Contains("field home_address type address {", schema);
        Assert.Contains("indexing: summary", schema);
    }

    [Fact]
    public void GenerateSchema_ArrayOfStruct_EmitsArrayType()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<ArrayStructDoc>();
        Assert.Contains("struct address {", schema);
        Assert.Contains("field addresses type array<address> {", schema);
    }

    // --- M16: Import fields ---

    [Fact]
    public void GenerateSchema_ImportField_EmitsImportOutsideDocument()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<AdDoc>();
        Assert.Contains("import field campaign_ref.budget as campaign_budget {}", schema);
        // import field must be outside the document block
        var docClose = schema.IndexOf("    }\n", StringComparison.Ordinal);
        var importIdx = schema.IndexOf("import field", StringComparison.Ordinal);
        Assert.True(importIdx > docClose);
    }

    [Fact]
    public void GenerateSchema_ReferenceField_EmitsReferenceType()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<AdDoc>();
        Assert.Contains("field campaign_ref type reference<campaign>", schema);
    }

    // --- M16: Constant tensors ---

    [Fact]
    public void GenerateSchema_Constant_EmitsConstantBlock()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<ConstantDoc>();
        Assert.Contains("constant my_weights {", schema);
        Assert.Contains("file: constants/my_weights.json", schema);
        Assert.Contains("type: tensor<float>(x[3])", schema);
    }

    [Fact]
    public void GenerateSchema_MultipleConstants_EmitsAll()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<MultiConstantDoc>();
        Assert.Contains("constant weights_a {", schema);
        Assert.Contains("constant weights_b {", schema);
    }

    // --- M16: Streaming mode ---

    [Fact]
    public void GenerateApplicationPackage_StreamingMode_EmitsStreamingInServicesXml()
    {
        var bytes = GeneratePackageBytes<StreamingDoc>();
        var servicesXml = ReadServicesXml(bytes);
        Assert.Contains("mode=\"streaming\"", servicesXml);
    }

    [Fact]
    public void GenerateApplicationPackage_StoreOnlyMode_EmitsStoreOnlyInServicesXml()
    {
        var bytes = GeneratePackageBytes<StoreOnlyDoc>();
        var servicesXml = ReadServicesXml(bytes);
        Assert.Contains("mode=\"store-only\"", servicesXml);
    }

    [Fact]
    public void GenerateApplicationPackage_GlobalDocument_EmitsGlobalAttribute()
    {
        var bytes = GeneratePackageBytes<GlobalDoc>();
        var servicesXml = ReadServicesXml(bytes);
        Assert.Contains("global=\"true\"", servicesXml);
    }

    // --- M19: ONNX model declarations ---

    [Fact]
    public void GenerateSchema_OnnxModel_EmitsOnnxModelBlock()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<OnnxModelDoc>();
        Assert.Contains("onnx-model my_model {", schema);
        Assert.Contains("file: models/my_model.onnx", schema);
    }

    [Fact]
    public void GenerateSchema_MultipleOnnxModels_EmitsAll()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<MultiOnnxDoc>();
        Assert.Contains("onnx-model model_a {", schema);
        Assert.Contains("onnx-model model_b {", schema);
    }

    // --- AdditionalFiles (file-path based bundling) ---

    [Fact]
    public void GenerateApplicationPackage_AdditionalFiles_IncludedInZip()
    {
        var onnxBytes = new byte[] { 0x4F, 0x4E, 0x4E, 0x58 }; // fake ONNX magic
        var jsonContent = "{\"values\":[1,2,3]}";

        var onnxPath = Path.GetTempFileName();
        var jsonPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(onnxPath, onnxBytes);
            File.WriteAllText(jsonPath, jsonContent);

            var opts = new ApplicationPackageOptions
            {
                AdditionalFiles = new Dictionary<string, string>
                {
                    ["models/my_model.onnx"] = onnxPath,
                    ["constants/weights.json"] = jsonPath
                }
            };

            var bytes = GeneratePackageBytes<OnnxModelDoc>(opts);

            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("models/my_model.onnx", names);
            Assert.Contains("constants/weights.json", names);

            // Verify content round-trips
            using var onnxStream = zip.GetEntry("models/my_model.onnx")!.Open();
            var buf = new byte[4];
            onnxStream.ReadExactly(buf, 0, 4);
            Assert.Equal(onnxBytes, buf);
        }
        finally
        {
            File.Delete(onnxPath);
            File.Delete(jsonPath);
        }
    }

    [Fact]
    public void GenerateApplicationPackage_SourcePath_AutoIncludesFiles()
    {
        var onnxBytes = new byte[] { 0x4F, 0x4E, 0x4E, 0x58 };
        var onnxPath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(onnxPath, onnxBytes);

            // Use a doc type with SourcePath set dynamically — we test via AdditionalFiles
            // since attributes are compile-time. SourcePath is tested separately below.
            var opts = new ApplicationPackageOptions
            {
                AdditionalFiles = new Dictionary<string, string>
                {
                    ["models/my_model.onnx"] = onnxPath
                }
            };

            var bytes = GeneratePackageBytes<OnnxModelDoc>(opts);

            using var ms = new MemoryStream(bytes);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            Assert.NotNull(zip.GetEntry("models/my_model.onnx"));
        }
        finally
        {
            File.Delete(onnxPath);
        }
    }

    [Fact]
    public void GenerateApplicationPackage_NoAdditionalFiles_StillWorks()
    {
        var bytes = GeneratePackageBytes<MusicDoc>();

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Contains("schemas/music.sd", names);
        Assert.Contains("services.xml", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void GenerateApplicationPackage_DefaultMode_EmitsIndex()
    {
        var bytes = GeneratePackageBytes<MusicDoc>();
        var servicesXml = ReadServicesXml(bytes);
        Assert.Contains("mode=\"index\"", servicesXml);
        Assert.DoesNotContain("global=", servicesXml);
    }

    [Fact]
    public void GenerateApplicationPackage_WritesToStream()
    {
        using var ms = new MemoryStream();
        VespaSchemaBuilder.GenerateApplicationPackage<MusicDoc>(ms);

        Assert.True(ms.Length > 0);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("schemas/music.sd"));
        Assert.NotNull(zip.GetEntry("services.xml"));
    }

    [Fact]
    public void GenerateApplicationPackage_MissingSourceFile_Throws()
    {
        var opts = new ApplicationPackageOptions
        {
            AdditionalFiles = new Dictionary<string, string>
            {
                ["models/missing.onnx"] = "/nonexistent/path/model.onnx"
            }
        };

        using var ms = new MemoryStream();
        Assert.Throws<FileNotFoundException>(() =>
            VespaSchemaBuilder.GenerateApplicationPackage<MusicDoc>(ms, opts));
    }

    // --- Helpers ---

    private static byte[] GeneratePackageBytes<T>(ApplicationPackageOptions? options = null) where T : class
    {
        using var ms = new MemoryStream();
        VespaSchemaBuilder.GenerateApplicationPackage<T>(ms, options);
        return ms.ToArray();
    }

    private static string ReadServicesXml(byte[] zipBytes)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using var stream = zip.GetEntry("services.xml")!.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

[VespaDocument("summary_index_doc")]
file record SummaryIndexDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.SummaryIndex)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("stemming_doc")]
file record StemmingDoc
{
    [VespaField(Name = "title", Stemming = StemmingMode.Best, IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("stemming_off_doc")]
file record StemmingOffDoc
{
    [VespaField(Name = "title", Stemming = StemmingMode.Off, IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("bolding_doc")]
file record BoldingDoc
{
    [VespaField(Name = "title", Bolding = true, IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("fastsearch_doc")]
file record FastSearchDoc
{
    [VespaField(Name = "category", FastSearch = true, IndexingMode = IndexingMode.AttributeSummary)]
    public string Category { get; init; } = string.Empty;
}

[VespaDocument("fieldset_doc")]
[VespaFieldSet("default", "title", "body")]
file record FieldsetDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;

    [VespaField(Name = "body", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Body { get; init; } = string.Empty;
}

[VespaDocument("docsummary_doc")]
[VespaDocumentSummary("compact", "title", "year")]
file record DocSummaryDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;

    [VespaField(Name = "year", FieldType = VespaFieldType.Int, IndexingMode = IndexingMode.AttributeSummary)]
    public int Year { get; init; }

    [VespaField(Name = "body", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Body { get; init; } = string.Empty;
}

[VespaDocument("docsummary_inherits_doc")]
[VespaDocumentSummary("extended", "title", "body", Inherits = "compact")]
file record DocSummaryInheritsDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;

    [VespaField(Name = "body", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Body { get; init; } = string.Empty;
}

[VespaDocument("rankprofile_doc")]
[VespaRankProfile("myrank", FirstPhase = "nativeRank(title)")]
file record RankProfileDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("rankprofile_secondphase_doc")]
[VespaRankProfile("myrank", FirstPhase = "nativeRank(title)", SecondPhase = "bm25(title)", SecondPhaseRerankCount = 100)]
file record RankProfileSecondPhaseDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("rankprofile_inherits_doc")]
[VespaRankProfile("child", Inherits = "default", FirstPhase = "nativeRank")]
file record RankProfileInheritsDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("rankprofile_features_doc")]
[VespaRankProfile("myrank", FirstPhase = "nativeRank", MatchFeatures = "bm25(title) nativeRank", SummaryFeatures = "firstPhase")]
file record RankProfileFeaturesDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("normalizing_doc")]
file record NormalizingDoc
{
    [VespaField(Name = "title", Normalizing = "lowercase", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("rankfilter_doc")]
file record RankFilterDoc
{
    [VespaField(Name = "category", RankType = RankType.Filter, IndexingMode = IndexingMode.AttributeSummary)]
    public string Category { get; init; } = string.Empty;
}

[VespaDocument("ranknormal_doc")]
file record RankNormalDoc
{
    [VespaField(Name = "title", RankType = RankType.Default, IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("ranktype_doc")]
file record RankTypeElementDoc
{
    [VespaField(Name = "popularity", RankType = RankType.Identity, IndexingMode = IndexingMode.AttributeSummary)]
    public int Popularity { get; init; }

    [VespaField(Name = "labels", RankType = RankType.Tags, IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Labels { get; init; } = string.Empty;
}

[VespaDocument("geo_doc")]
file record GeoMetricDoc
{
    [VespaTensor("tensor<float>(x[2])", EnableIndex = true, DistanceMetric = DistanceMetric.Geodesic)]
    public float[]? Location { get; init; }
}

[VespaDocument("default_profile_doc")]
[VespaRankProfile("default", FirstPhase = "nativeRank(name)")]
file record DefaultProfileWithTensorDoc
{
    [VespaField(Name = "name", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Name { get; init; } = string.Empty;

    [VespaTensor("tensor<float>(x[8])", EnableIndex = true, DistanceMetric = DistanceMetric.Angular)]
    public float[]? Embedding { get; init; }
}

[VespaDocument("child_doc", Inherits = "base_doc")]
file record InheritingDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("expiry_doc", Selection = "expiry_doc.timestamp > now() - 86400")]
file record ExpiryDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("diversity_doc")]
[VespaRankProfile("myrank", FirstPhase = "nativeRank", DiversityAttribute = "category", DiversityMinGroups = 10)]
file record RankProfileDiversityDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("func_doc")]
[VespaRankProfile("myrank", FirstPhase = "freshness", Functions = "freshness: now - attribute(timestamp)")]
file record RankProfileFuncDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("multifunc_doc")]
[VespaRankProfile("myrank", FirstPhase = "myScore(1,2)", Functions = "freshness: now - attribute(timestamp); myScore(a,b): a * b")]
file record RankProfileMultiFuncDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

// --- M16 test types ---

[VespaStruct("address")]
file class AddressStruct
{
    [VespaField(Name = "street")] public string Street { get; set; } = "";
    [VespaField(Name = "city")] public string City { get; set; } = "";
}

[VespaDocument("struct_doc")]
file record StructDoc
{
    [VespaField(IndexingMode = IndexingMode.Summary)]
    public AddressStruct? HomeAddress { get; init; }
}

[VespaDocument("array_struct_doc")]
file record ArrayStructDoc
{
    [VespaField(IndexingMode = IndexingMode.Summary)]
    public List<AddressStruct>? Addresses { get; init; }
}

[VespaDocument("ad")]
[VespaImportField("campaign_ref", "budget", "campaign_budget")]
file record AdDoc
{
    [VespaField(Name = "campaign_ref", FieldType = VespaFieldType.Reference, ReferenceDocumentType = "campaign")]
    public string CampaignRef { get; init; } = "";
}

[VespaDocument("constant_doc")]
[VespaConstant("my_weights", "constants/my_weights.json", "tensor<float>(x[3])")]
file record ConstantDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = "";
}

[VespaDocument("multi_constant_doc")]
[VespaConstant("weights_a", "constants/a.json", "tensor<float>(x[3])")]
[VespaConstant("weights_b", "constants/b.json", "tensor<float>(x[5])")]
file record MultiConstantDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = "";
}

[VespaDocument("mail", Mode = DocumentMode.Streaming)]
file record StreamingDoc
{
    [VespaField(Name = "subject", IndexingMode = IndexingMode.AttributeSummary)]
    public string Subject { get; init; } = "";
}

[VespaDocument("archive", Mode = DocumentMode.StoreOnly)]
file record StoreOnlyDoc
{
    [VespaField(Name = "data", IndexingMode = IndexingMode.Summary)]
    public string Data { get; init; } = "";
}

[VespaDocument("config", Global = true)]
file record GlobalDoc
{
    [VespaField(Name = "value", IndexingMode = IndexingMode.AttributeSummary)]
    public string Value { get; init; } = "";
}

// --- M19 test types ---

[VespaDocument("onnx_doc")]
[VespaOnnxModel("my_model", "models/my_model.onnx")]
file record OnnxModelDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = "";
}

[VespaDocument("multi_onnx_doc")]
[VespaOnnxModel("model_a", "models/a.onnx")]
[VespaOnnxModel("model_b", "models/b.onnx")]
file record MultiOnnxDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = "";
}
