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
    public void GenerateSchema_Inherits_IncludesSchemaAndDocumentInherits()
    {
        // Vespa requires the document type to redeclare the inheritance;
        // `schema X inherits Y` alone is rejected at deployment.
        var schema = VespaSchemaBuilder.GenerateSchema<InheritingDoc>();
        Assert.Contains("schema child_doc inherits base_doc {", schema);
        Assert.Contains("document child_doc inherits base_doc {", schema);
    }

    // --- M14: Document expiry ---

    [Fact]
    public void GenerateSchema_Selection_NotEmittedInSchema()
    {
        // `selection:` inside the document block is invalid — it belongs in services.xml
        var schema = VespaSchemaBuilder.GenerateSchema<ExpiryDoc>();
        Assert.DoesNotContain("selection", schema);
    }

    [Fact]
    public void GenerateApplicationPackage_Selection_EmitsSelectionAndGarbageCollectionInServicesXml()
    {
        var bytes = GeneratePackageBytes<ExpiryDoc>();
        var servicesXml = ReadServicesXml(bytes);

        Assert.Contains("<documents garbage-collection=\"true\">", servicesXml);
        // XML-escaped selection expression on the <document> element
        Assert.Contains(
            "selection=\"expiry_doc.timestamp &gt; now() - 86400\"",
            servicesXml);
    }

    [Fact]
    public void GenerateApplicationPackage_NoSelection_OmitsGarbageCollection()
    {
        var servicesXml = ReadServicesXml(GeneratePackageBytes<MusicDoc>());
        Assert.DoesNotContain("garbage-collection", servicesXml);
        Assert.DoesNotContain("selection=", servicesXml);
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
        // Legacy string DSL — obsolete but still honored
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

    [Fact]
    public void GenerateSchema_RankProfileFunctions_MissingColon_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VespaSchemaBuilder.GenerateSchema<RankProfileBadFuncDoc>());
        Assert.Contains("signature: expression", ex.Message);
    }

    // --- [VespaRankFunction] attribute ---

    [Fact]
    public void GenerateSchema_RankFunctionAttribute_SingleProfile_EmitsFunction()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankFunctionAttrDoc>();
        Assert.Contains("function freshness {", schema);
        Assert.Contains("expression: now - attribute(timestamp)", schema);
        // Expressions containing ':' (impossible in the string DSL) work
        Assert.Contains("function scaled(x) {", schema);
        Assert.Contains("expression: if (x > 0, x, 0)", schema);
    }

    [Fact]
    public void GenerateSchema_RankFunctionAttribute_ExplicitProfile_TargetsProfile()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankFunctionMultiProfileDoc>();

        var profileA = schema.IndexOf("rank-profile profile_a", StringComparison.Ordinal);
        var profileB = schema.IndexOf("rank-profile profile_b", StringComparison.Ordinal);
        var funcIdx = schema.IndexOf("function only_in_b {", StringComparison.Ordinal);
        Assert.True(funcIdx > profileB && profileB > profileA);
    }

    [Fact]
    public void GenerateSchema_RankFunctionAttribute_AmbiguousProfile_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VespaSchemaBuilder.GenerateSchema<RankFunctionAmbiguousDoc>());
        Assert.Contains("ambiguous", ex.Message);
    }

    [Fact]
    public void GenerateSchema_RankFunctionAttribute_UnknownProfile_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VespaSchemaBuilder.GenerateSchema<RankFunctionUnknownProfileDoc>());
        Assert.Contains("not declared", ex.Message);
    }

    [Fact]
    public void GenerateSchema_RankFunctionAttribute_NoProfile_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VespaSchemaBuilder.GenerateSchema<RankFunctionNoProfileDoc>());
        Assert.Contains("requires a rank profile", ex.Message);
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

    [Fact]
    public void GenerateSchema_SingleStructField_AttributeMode_Throws()
    {
        // Vespa's ComplexFieldsWithStructFieldAttributesValidator rejects struct-field
        // attributes on single struct fields (only array-of-struct and map are supported),
        // so the default AttributeSummary must throw instead of generating an
        // undeployable schema.
        var ex = Assert.Throws<InvalidOperationException>(
            () => VespaSchemaBuilder.GenerateSchema<StructAttributeDoc>());
        Assert.Contains("single struct fields", ex.Message);
        Assert.Contains("IndexingMode.Summary", ex.Message);
    }

    [Fact]
    public void GenerateSchema_ArrayOfStruct_DefaultAttributeSummary_EmitsSummaryAndStructFields()
    {
        // The default IndexingMode (AttributeSummary) must not produce the invalid
        // `indexing: attribute | summary` on an array-of-struct field; Attribute
        // translates to struct-field attribute blocks for primitive members instead.
        var schema = VespaSchemaBuilder.GenerateSchema<ArrayStructAttributeDoc>();

        Assert.Contains("field addresses type array<address> {", schema);
        Assert.Contains("indexing: summary", schema);
        Assert.DoesNotContain("attribute | summary", schema);
        Assert.Contains("struct-field street { indexing: attribute }", schema);
        Assert.Contains("struct-field city { indexing: attribute }", schema);
    }

    [Fact]
    public void GenerateSchema_StructField_IndexMode_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VespaSchemaBuilder.GenerateSchema<StructIndexDoc>());
        Assert.Contains("IndexingMode.Index is not supported for struct fields", ex.Message);
    }

    [Fact]
    public void GenerateSchema_MapField_EmitsSummaryAndKeyValueStructFields()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<MapDoc>();

        Assert.Contains("field labels type map<string,string> {", schema);
        Assert.Contains("indexing: summary", schema);
        Assert.Contains("struct-field key { indexing: attribute }", schema);
        Assert.Contains("struct-field value { indexing: attribute }", schema);
    }

    [Fact]
    public void GenerateSchema_MapOfStruct_EmitsValueMemberStructFields()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<MapOfStructDoc>();

        Assert.Contains("field contacts type map<string,address> {", schema);
        Assert.Contains("struct-field key { indexing: attribute }", schema);
        Assert.Contains("struct-field value.street { indexing: attribute }", schema);
        Assert.Contains("struct-field value.city { indexing: attribute }", schema);
    }

    [Fact]
    public void GenerateSchema_MapField_SummaryOnly_OmitsStructFields()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<MapSummaryOnlyDoc>();

        Assert.Contains("field labels type map<string,string> {", schema);
        Assert.Contains("indexing: summary", schema);
        Assert.DoesNotContain("struct-field", schema);
    }

    [Fact]
    public void GenerateSchema_NestedStruct_EmitsBothStructDefinitions()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<NestedStructDoc>();

        Assert.Contains("struct geo_point {", schema);
        Assert.Contains("struct place {", schema);
        Assert.Contains("field location type geo_point {}", schema);
        // Nested struct must be defined before the struct referencing it
        Assert.True(
            schema.IndexOf("struct geo_point {", StringComparison.Ordinal)
            < schema.IndexOf("struct place {", StringComparison.Ordinal));
    }

    [Fact]
    public void GenerateSchema_RecursiveStruct_Throws()
    {
        // Vespa schemas cannot express recursive struct types — must fail with a
        // clear message instead of overflowing the stack during collection.
        var ex = Assert.Throws<NotSupportedException>(
            () => VespaSchemaBuilder.GenerateSchema<RecursiveStructDoc>());
        Assert.Contains("recursive", ex.Message);
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

    // --- Type inference: widened numeric/collection mapping ---

    [Fact]
    public void GenerateSchema_WidenedScalars_MapCorrectly()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<WidenedScalarsDoc>();

        Assert.Contains("field short_val type int", schema);
        Assert.Contains("field ushort_val type int", schema);
        Assert.Contains("field uint_val type long", schema);
        Assert.Contains("field sbyte_val type byte", schema);
        Assert.Contains("field guid_val type string", schema);
        Assert.Contains("field char_val type string", schema);
        Assert.Contains("field blob type raw", schema);
    }

    [Fact]
    public void GenerateSchema_ReadOnlyCollections_MapToArray()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<CollectionsDoc>();

        Assert.Contains("field ro_list type array<int>", schema);
        Assert.Contains("field ro_collection type array<string>", schema);
        Assert.Contains("field hash_set type array<string>", schema);
        Assert.Contains("field set_iface type array<long>", schema);
        Assert.Contains("field weights type weightedset<string>", schema);
    }

    [Fact]
    public void GenerateSchema_CSharpByteField_Throws()
    {
        // C# byte is 0..255, Vespa byte is signed -128..127: values 128..255 fail at feed
        var ex = Assert.Throws<NotSupportedException>(
            () => VespaSchemaBuilder.GenerateSchema<ByteFieldDoc>());
        Assert.Contains("sbyte", ex.Message);
    }

    [Fact]
    public void GenerateSchema_UlongField_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => VespaSchemaBuilder.GenerateSchema<UlongFieldDoc>());
        Assert.Contains("unsigned 64-bit", ex.Message);
    }

    [Fact]
    public void GenerateSchema_EnumField_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => VespaSchemaBuilder.GenerateSchema<EnumFieldDoc>());
        Assert.Contains("JsonStringEnumConverter", ex.Message);
    }

    [Fact]
    public void GenerateSchema_UnmappableField_Throws()
    {
        // Unknown types must throw instead of silently mapping to string
        var ex = Assert.Throws<NotSupportedException>(
            () => VespaSchemaBuilder.GenerateSchema<DateTimeFieldDoc>());
        Assert.Contains("Cannot map CLR type 'DateTime'", ex.Message);
    }

    // --- Predicate fields ---

    [Fact]
    public void GenerateSchema_PredicateField_EmitsArityBlock()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<PredicateDoc>();

        Assert.Contains("field constraints type predicate {", schema);
        Assert.Contains("indexing: attribute | summary", schema);
        Assert.Contains("arity: 8", schema);
        Assert.Contains("lower-bound: 3", schema);
        Assert.Contains("upper-bound: 200", schema);
    }

    [Fact]
    public void GenerateSchema_PredicateField_WithoutArity_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VespaSchemaBuilder.GenerateSchema<PredicateNoArityDoc>());
        Assert.Contains("PredicateArity", ex.Message);
    }

    [Fact]
    public void GenerateSchema_PredicateArityOnNonPredicate_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VespaSchemaBuilder.GenerateSchema<ArityOnStringDoc>());
        Assert.Contains("non-predicate", ex.Message);
    }

    // --- Tensor knobs: IndexType / [VespaField] IndexingMode ---

    [Fact]
    public void GenerateSchema_TensorIndexRequested_WithIndexTypeNone_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VespaSchemaBuilder.GenerateSchema<TensorIndexNoneDoc>());
        Assert.Contains("TensorIndexType.None", ex.Message);
    }

    [Fact]
    public void GenerateSchema_TensorWithFieldSummaryMode_IncludesSummary()
    {
        // [VespaField(IndexingMode = AttributeSummary)] on a tensor property is honored
        var schema = VespaSchemaBuilder.GenerateSchema<TensorFieldModeDoc>();
        Assert.Contains("indexing: attribute | summary", schema);
    }

    [Fact]
    public void GenerateSchema_TensorWithFieldIndexMode_EnablesHnsw()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<TensorFieldIndexModeDoc>();
        Assert.Contains("indexing: attribute | index", schema);
        Assert.Contains("hnsw", schema);
    }

    [Fact]
    public void GenerateSchema_ImplicitDoubleTensorType_Parses()
    {
        // tensor(x{}) — value type omitted — defaults to double per the tensor grammar
        var schema = VespaSchemaBuilder.GenerateSchema<ImplicitDoubleTensorDoc>();
        Assert.Contains("field sparse type tensor<double>(x{})", schema);
    }

    // --- Distance metric renames ---

    [Fact]
    public void GenerateSchema_InnerProductMetric_EmitsPrenormalizedAngular()
    {
        // Vespa removed the "innerproduct" name; its successor is prenormalized-angular
        var schema = VespaSchemaBuilder.GenerateSchema<InnerProductMetricDoc>();
        Assert.Contains("distance-metric: prenormalized-angular", schema);
        Assert.DoesNotContain("distance-metric: innerproduct", schema);
    }

    // --- Stemming shortest / rank-type about, empty ---

    [Fact]
    public void GenerateSchema_StemmingShortest_IncludesShortest()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<StemmingShortestDoc>();
        Assert.Contains("stemming: shortest", schema);
    }

    [Fact]
    public void GenerateSchema_RankAboutAndEmpty_EmitRankTypeElement()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<RankAboutEmptyDoc>();
        Assert.Contains("rank-type: about", schema);
        Assert.Contains("rank-type: empty", schema);
    }

    // --- Rank profile inputs (C9) ---

    [Fact]
    public void GenerateSchema_NonInheritingProfile_GetsTensorInputs()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<StandaloneProfileWithTensorDoc>();

        // Both the auto-generated default and the standalone profile carry the inputs
        var standalone = schema.IndexOf("rank-profile standalone {", StringComparison.Ordinal);
        Assert.True(standalone >= 0);
        var inputsAfter = schema.IndexOf("inputs {", standalone, StringComparison.Ordinal);
        Assert.True(inputsAfter > standalone);
    }

    [Fact]
    public void GenerateSchema_ProfileInheritingDefault_DoesNotRedeclareInputs()
    {
        var schema = VespaSchemaBuilder.GenerateSchema<InheritingProfileWithTensorDoc>();

        var child = schema.IndexOf("rank-profile child inherits default {", StringComparison.Ordinal);
        Assert.True(child >= 0);
        Assert.Equal(-1, schema.IndexOf("inputs {", child, StringComparison.Ordinal));
    }

    // --- Multi-node packages (hosts.xml) ---

    [Fact]
    public void GenerateApplicationPackage_MultiNode_GeneratesHostsXml()
    {
        var opts = new ApplicationPackageOptions { NodeCount = 3, Redundancy = 2 };
        var bytes = GeneratePackageBytes<MusicDoc>(opts);

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        using var reader = new StreamReader(zip.GetEntry("hosts.xml")!.Open());
        var hostsXml = reader.ReadToEnd();
        Assert.Contains("<host name=\"node1\">", hostsXml);
        Assert.Contains("<alias>node1</alias>", hostsXml);
        Assert.Contains("<alias>node3</alias>", hostsXml);

        var servicesXml = ReadServicesXml(bytes);
        Assert.Contains("<node hostalias=\"node3\" distribution-key=\"2\"/>", servicesXml);
        // Multi-node containers need explicit nodes referencing the aliases
        Assert.Contains("<node hostalias=\"node1\"/>", servicesXml);
    }

    [Fact]
    public void GenerateApplicationPackage_MultiNode_WithHostNames_UsesThem()
    {
        var opts = new ApplicationPackageOptions
        {
            NodeCount = 2,
            HostNames = ["vespa0.example.com", "vespa1.example.com"]
        };
        var bytes = GeneratePackageBytes<MusicDoc>(opts);

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("hosts.xml")!.Open());
        var hostsXml = reader.ReadToEnd();

        Assert.Contains("<host name=\"vespa0.example.com\">", hostsXml);
        Assert.Contains("<host name=\"vespa1.example.com\">", hostsXml);
    }

    [Fact]
    public void GenerateApplicationPackage_SingleNode_HasNoHostsXml()
    {
        var bytes = GeneratePackageBytes<MusicDoc>();

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.Null(zip.GetEntry("hosts.xml"));
    }

    [Fact]
    public void GenerateApplicationPackage_HostNamesCountMismatch_Throws()
    {
        var opts = new ApplicationPackageOptions { NodeCount = 3, HostNames = ["a"] };
        using var ms = new MemoryStream();
        var ex = Assert.Throws<ArgumentException>(
            () => VespaSchemaBuilder.GenerateApplicationPackage<MusicDoc>(ms, opts));
        Assert.Contains("HostNames", ex.Message);
    }

    [Fact]
    public void GenerateApplicationPackage_RedundancyExceedsNodeCount_Throws()
    {
        var opts = new ApplicationPackageOptions { NodeCount = 1, Redundancy = 2 };
        using var ms = new MemoryStream();
        var ex = Assert.Throws<ArgumentException>(
            () => VespaSchemaBuilder.GenerateApplicationPackage<MusicDoc>(ms, opts));
        Assert.Contains("Redundancy", ex.Message);
    }

    // --- AdditionalFiles vs generated entries ---

    [Fact]
    public void GenerateApplicationPackage_AdditionalFileCollidingWithServicesXml_Throws()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var opts = new ApplicationPackageOptions
            {
                AdditionalFiles = new Dictionary<string, string> { ["services.xml"] = tmp }
            };
            using var ms = new MemoryStream();
            var ex = Assert.Throws<InvalidOperationException>(
                () => VespaSchemaBuilder.GenerateApplicationPackage<MusicDoc>(ms, opts));
            Assert.Contains("CustomServicesXml", ex.Message);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void GenerateApplicationPackage_AdditionalFileCollidingWithSchema_Throws()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var opts = new ApplicationPackageOptions
            {
                AdditionalFiles = new Dictionary<string, string> { ["schemas/music.sd"] = tmp }
            };
            using var ms = new MemoryStream();
            Assert.Throws<InvalidOperationException>(
                () => VespaSchemaBuilder.GenerateApplicationPackage<MusicDoc>(ms, opts));
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // --- services.xml escaping ---

    [Fact]
    public void GenerateApplicationPackage_SelectionWithXmlSpecialChars_IsEscaped()
    {
        var servicesXml = ReadServicesXml(GeneratePackageBytes<EscapingSelectionDoc>());
        Assert.Contains("a &lt; 1 &amp;&amp; b &gt; 2", servicesXml);
        Assert.DoesNotContain("a < 1 && b > 2", servicesXml);
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

#pragma warning disable CS0618 // Functions DSL is obsolete but must keep working
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

[VespaDocument("badfunc_doc")]
[VespaRankProfile("myrank", Functions = "freshness now - attribute(timestamp)")]
file record RankProfileBadFuncDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}
#pragma warning restore CS0618

[VespaDocument("rankfunc_attr_doc")]
[VespaRankProfile("myrank", FirstPhase = "freshness")]
[VespaRankFunction("freshness", "now - attribute(timestamp)")]
[VespaRankFunction("scaled(x)", "if (x > 0, x, 0)")]
file record RankFunctionAttrDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = string.Empty;
}

[VespaDocument("rankfunc_multi_doc")]
[VespaRankProfile("profile_a", FirstPhase = "nativeRank")]
[VespaRankProfile("profile_b", FirstPhase = "only_in_b")]
[VespaRankFunction("only_in_b", "attribute(score) * 2", Profile = "profile_b")]
file record RankFunctionMultiProfileDoc
{
    [VespaField(Name = "score", IndexingMode = IndexingMode.AttributeSummary)]
    public double Score { get; init; }
}

[VespaDocument("rankfunc_ambiguous_doc")]
[VespaRankProfile("profile_a", FirstPhase = "nativeRank")]
[VespaRankProfile("profile_b", FirstPhase = "nativeRank")]
[VespaRankFunction("shared", "attribute(score)")]
file record RankFunctionAmbiguousDoc
{
    [VespaField(Name = "score", IndexingMode = IndexingMode.AttributeSummary)]
    public double Score { get; init; }
}

[VespaDocument("rankfunc_unknown_doc")]
[VespaRankProfile("profile_a", FirstPhase = "nativeRank")]
[VespaRankFunction("orphan", "attribute(score)", Profile = "nope")]
file record RankFunctionUnknownProfileDoc
{
    [VespaField(Name = "score", IndexingMode = IndexingMode.AttributeSummary)]
    public double Score { get; init; }
}

[VespaDocument("rankfunc_noprofile_doc")]
[VespaRankFunction("orphan", "attribute(score)")]
file record RankFunctionNoProfileDoc
{
    [VespaField(Name = "score", IndexingMode = IndexingMode.AttributeSummary)]
    public double Score { get; init; }
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

// --- Block C test types: struct-field, mapping policy, predicate, tensors, packages ---

[VespaDocument("struct_attr_doc")]
file record StructAttributeDoc
{
    [VespaField] // default AttributeSummary is invalid on a single struct field → throws
    public AddressStruct? HomeAddress { get; init; }
}

[VespaDocument("array_struct_attr_doc")]
file record ArrayStructAttributeDoc
{
    [VespaField] // default AttributeSummary → summary + struct-field attribute blocks
    public List<AddressStruct>? Addresses { get; init; }
}

[VespaStruct("tree_node")]
file class RecursiveNodeStruct
{
    [VespaField(Name = "label")] public string Label { get; set; } = "";
    [VespaField(Name = "child")] public RecursiveNodeStruct? Child { get; set; }
}

[VespaDocument("recursive_struct_doc")]
file record RecursiveStructDoc
{
    [VespaField(IndexingMode = IndexingMode.Summary)]
    public RecursiveNodeStruct? Root { get; init; }
}

[VespaDocument("struct_index_doc")]
file record StructIndexDoc
{
    [VespaField(IndexingMode = IndexingMode.SummaryIndex)]
    public AddressStruct? HomeAddress { get; init; }
}

[VespaDocument("map_doc")]
file record MapDoc
{
    [VespaField(Name = "labels")]
    public Dictionary<string, string>? Labels { get; init; }
}

[VespaDocument("map_of_struct_doc")]
file record MapOfStructDoc
{
    [VespaField(Name = "contacts")]
    public Dictionary<string, AddressStruct>? Contacts { get; init; }
}

[VespaDocument("map_summary_doc")]
file record MapSummaryOnlyDoc
{
    [VespaField(Name = "labels", IndexingMode = IndexingMode.Summary)]
    public Dictionary<string, string>? Labels { get; init; }
}

[VespaStruct("geo_point")]
file class GeoPointStruct
{
    [VespaField(Name = "lat")] public double Lat { get; set; }
    [VespaField(Name = "lng")] public double Lng { get; set; }
}

[VespaStruct("place")]
file class PlaceStruct
{
    [VespaField(Name = "name")] public string Name { get; set; } = "";
    [VespaField(Name = "location")] public GeoPointStruct? Location { get; set; }
}

[VespaDocument("nested_struct_doc")]
file record NestedStructDoc
{
    [VespaField(IndexingMode = IndexingMode.Summary)]
    public PlaceStruct? Place { get; init; }
}

[VespaDocument("widened_scalars_doc")]
file record WidenedScalarsDoc
{
    [VespaField(Name = "short_val")] public short ShortVal { get; init; }
    [VespaField(Name = "ushort_val")] public ushort UshortVal { get; init; }
    [VespaField(Name = "uint_val")] public uint UintVal { get; init; }
    [VespaField(Name = "sbyte_val")] public sbyte SbyteVal { get; init; }
    [VespaField(Name = "guid_val")] public Guid GuidVal { get; init; }
    [VespaField(Name = "char_val")] public char CharVal { get; init; }
    [VespaField(Name = "blob", IndexingMode = IndexingMode.Summary)] public byte[]? Blob { get; init; }
}

[VespaDocument("collections_doc")]
file record CollectionsDoc
{
    [VespaField(Name = "ro_list")] public IReadOnlyList<int>? RoList { get; init; }
    [VespaField(Name = "ro_collection")] public IReadOnlyCollection<string>? RoCollection { get; init; }
    [VespaField(Name = "hash_set")] public HashSet<string>? HashSetVals { get; init; }
    [VespaField(Name = "set_iface")] public ISet<long>? SetIface { get; init; }
    [VespaField(Name = "weights")] public Dictionary<string, int>? Weights { get; init; }
}

[VespaDocument("byte_field_doc")]
file record ByteFieldDoc
{
    [VespaField(Name = "flags")] public byte Flags { get; init; }
}

[VespaDocument("ulong_field_doc")]
file record UlongFieldDoc
{
    [VespaField(Name = "big")] public ulong Big { get; init; }
}

file enum TestColor { Red, Green }

[VespaDocument("enum_field_doc")]
file record EnumFieldDoc
{
    [VespaField(Name = "color")] public TestColor Color { get; init; }
}

[VespaDocument("datetime_field_doc")]
file record DateTimeFieldDoc
{
    [VespaField(Name = "created")] public DateTime Created { get; init; }
}

[VespaDocument("predicate_doc")]
file record PredicateDoc
{
    [VespaField(Name = "constraints", FieldType = VespaFieldType.Predicate,
        PredicateArity = 8, PredicateLowerBound = 3, PredicateUpperBound = 200)]
    public string Constraints { get; init; } = "";
}

[VespaDocument("predicate_noarity_doc")]
file record PredicateNoArityDoc
{
    [VespaField(Name = "constraints", FieldType = VespaFieldType.Predicate)]
    public string Constraints { get; init; } = "";
}

[VespaDocument("arity_on_string_doc")]
file record ArityOnStringDoc
{
    [VespaField(Name = "title", PredicateArity = 4)]
    public string Title { get; init; } = "";
}

[VespaDocument("tensor_index_none_doc")]
file record TensorIndexNoneDoc
{
    [VespaTensor("tensor<float>(x[8])", EnableIndex = true, IndexType = TensorIndexType.None)]
    public float[]? Vec { get; init; }
}

[VespaDocument("tensor_field_mode_doc")]
file record TensorFieldModeDoc
{
    [VespaField(Name = "vec", IndexingMode = IndexingMode.AttributeSummary)]
    [VespaTensor("tensor<float>(x[8])")]
    public float[]? Vec { get; init; }
}

[VespaDocument("tensor_field_index_mode_doc")]
file record TensorFieldIndexModeDoc
{
    [VespaField(Name = "vec", IndexingMode = IndexingMode.Attribute | IndexingMode.Index)]
    [VespaTensor("tensor<float>(x[8])")]
    public float[]? Vec { get; init; }
}

[VespaDocument("implicit_double_tensor_doc")]
file record ImplicitDoubleTensorDoc
{
    [VespaTensor("tensor(x{})", DistanceMetric = DistanceMetric.None)]
    public Dictionary<string, double>? Sparse { get; init; }
}

#pragma warning disable CS0618 // InnerProduct is obsolete; verify its mapped emission
[VespaDocument("ip_metric_doc")]
file record InnerProductMetricDoc
{
    [VespaTensor("tensor<float>(x[8])", DistanceMetric = DistanceMetric.InnerProduct)]
    public float[]? Vec { get; init; }
}
#pragma warning restore CS0618

[VespaDocument("stemming_shortest_doc")]
file record StemmingShortestDoc
{
    [VespaField(Name = "title", Stemming = StemmingMode.Shortest, IndexingMode = IndexingMode.IndexAttributeSummary)]
    public string Title { get; init; } = "";
}

[VespaDocument("rank_about_empty_doc")]
file record RankAboutEmptyDoc
{
    [VespaField(Name = "description", RankType = RankType.About, IndexingMode = IndexingMode.SummaryIndex)]
    public string Description { get; init; } = "";

    [VespaField(Name = "flag", RankType = RankType.Empty, IndexingMode = IndexingMode.AttributeSummary)]
    public string Flag { get; init; } = "";
}

[VespaDocument("standalone_profile_doc")]
[VespaRankProfile("standalone", FirstPhase = "nativeRank")]
file record StandaloneProfileWithTensorDoc
{
    [VespaTensor("tensor<float>(x[8])")]
    public float[]? Embedding { get; init; }
}

[VespaDocument("inheriting_profile_doc")]
[VespaRankProfile("child", Inherits = "default", FirstPhase = "nativeRank")]
file record InheritingProfileWithTensorDoc
{
    [VespaTensor("tensor<float>(x[8])")]
    public float[]? Embedding { get; init; }
}

[VespaDocument("escaping_doc", Selection = "a < 1 && b > 2")]
file record EscapingSelectionDoc
{
    [VespaField(Name = "title", IndexingMode = IndexingMode.AttributeSummary)]
    public string Title { get; init; } = "";
}
