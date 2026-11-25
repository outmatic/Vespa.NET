using System.Globalization;
using System.IO.Compression;
using Vespa.Models.Attributes;
using Vespa.Models.Schema;
using Xunit;

namespace Vespa.Tests.Unit;

public class RankProfileBuilderTests
{
    [Fact]
    public void Package_UsesTheFrozenProfilesForTheirOwnSchema()
    {
        var original = new ApplicationPackageOptions();
        RankProfileBuilder<RankedArticle> builder = null!;
        var parameters = new[] { "factor" };
        var options = original.WithRankProfile<RankedArticle>("hybrid-score", profile =>
        {
            builder = profile;
            profile.Inherits("default")
                .FirstPhase(0.75 * profile.Bm25(article => article.Headline) + profile.Closeness(article => article.Embedding))
                .SecondPhase(RankExpression.Call("boost", 2), rerankCount: 50)
                .Function("boost", profile.Attribute(article => article.Popularity) * RankExpression.Reference("factor"), parameters)
                .GlobalPhase(RankExpression.Raw("if(query(weight) > 0,\n    query(weight), 1)"), rerankCount: 10);
        });
        builder.FirstPhase(3);
        parameters[0] = "changed";

        var schema = VespaSchemaBuilder.GenerateSchema<RankedArticle>(options);
        Assert.Contains("rank-profile hybrid-score inherits default {", schema);
        Assert.Contains("index: enable-bm25", schema);
        Assert.Contains("expression: ((0.75 * bm25(article_title)) + closeness(field, vector_embedding))", schema);
        Assert.Contains("expression: boost(2)\n            rerank-count: 50", schema);
        Assert.Contains("function boost(factor)", schema);
        Assert.Contains("expression: (attribute(popularity_score) * factor)", schema);
        Assert.Contains("expression {\n                if(query(weight) > 0,", schema);
        Assert.Contains("rerank-count: 10", schema);
        Assert.Single(schema.Split("query(query_vector_embedding)", StringSplitOptions.None).Skip(1));
        Assert.DoesNotContain("hybrid-score", VespaSchemaBuilder.GenerateSchema<RankedArticle>(original));

        using var output = new MemoryStream();
        VespaSchemaBuilder.GenerateApplicationPackage(output, [typeof(RankedArticle), typeof(OtherArticle)], options);
        output.Position = 0;
        using var zip = new ZipArchive(output);
        using var articleSchema = new StreamReader(zip.GetEntry("schemas/ranked_article.sd")!.Open());
        Assert.Equal(schema, articleSchema.ReadToEnd());
        using var otherSchema = new StreamReader(zip.GetEntry("schemas/other_article.sd")!.Open());
        Assert.DoesNotContain("hybrid-score", otherSchema.ReadToEnd());
    }

    [Fact]
    public void Profiles_KeepAttributeFunctionTargetsAndAllowARuntimeDefault()
    {
        var options = new ApplicationPackageOptions()
            .WithRankProfile<OtherArticle>("runtime", p => p.FirstPhase(1))
            .WithRankProfile<RankedArticle>("default", p => p.FirstPhase(p.Closeness(a => a.Embedding)));

        var attributed = VespaSchemaBuilder.GenerateSchema<OtherArticle>(options);
        Assert.Contains("function original_score()", attributed.Split("rank-profile runtime")[0]);
        var runtime = VespaSchemaBuilder.GenerateSchema<RankedArticle>(options);
        Assert.Single(runtime.Split("rank-profile default {", StringSplitOptions.None).Skip(1));
        Assert.Contains("query(query_vector_embedding) tensor<float>(x[2])", runtime);
        Assert.Contains("expression: closeness(field, vector_embedding)", runtime);
    }

    [Fact]
    public void Profiles_RejectDuplicateNamesAndMissingTargets()
    {
        var options = new ApplicationPackageOptions()
            .WithRankProfile<OtherArticle>("runtime", p => p.FirstPhase(1));

        Assert.Throws<ArgumentException>("name", () => options.WithRankProfile<OtherArticle>(
            "runtime", _ => throw new InvalidOperationException("Duplicate profiles must fail before invoking the callback.")));
        var conflict = options.WithRankProfile<OtherArticle>("original", p => p.FirstPhase(1));
        Assert.Throws<InvalidOperationException>(() => VespaSchemaBuilder.GenerateSchema<OtherArticle>(conflict));
        using var output = new MemoryStream();
        Assert.Throws<ArgumentException>("options", () => VespaSchemaBuilder.GenerateApplicationPackage<RankedArticle>(output, options));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Expressions_PreserveOperandGroupingAndIgnoreCurrentCulture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
            var score = (RankExpression.Raw("x + y") - 0.5) / (RankExpression.Query("scale") * 2);
            Assert.Equal("(((x + y) - 0.5) / (query(scale) * 2))", score.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Expressions_RejectMalformedIdentifiersAndNonFiniteLiterals()
    {
        Assert.Throws<ArgumentException>("name", () => RankExpression.Call("max)", 1, 2));
        Assert.Throws<ArgumentException>("field", () => RankExpression.Attribute("foo..bar"));
        Assert.Throws<ArgumentOutOfRangeException>("value", () => RankExpression.Call("max", double.NaN));
        Assert.Throws<ArgumentException>("parameters", () => new ApplicationPackageOptions()
            .WithRankProfile<RankedArticle>("runtime", p => p.Function("score", 1, "x", "x")));
    }

    [Fact]
    public void Schema_RejectsBm25OnAnAttributeWithoutAnIndex()
    {
        var error = Assert.Throws<InvalidOperationException>(() => VespaSchemaBuilder.GenerateSchema<InvalidBm25Article>());
        Assert.Contains("EnableBm25 requires an indexed string field", error.Message);
    }

    [VespaDocument("ranked_article")]
    private sealed record RankedArticle
    {
        [VespaField(Name = "article_title", IndexingMode = IndexingMode.SummaryIndex, EnableBm25 = true)]
        public string Headline { get; init; } = "";

        [VespaField(Name = "popularity_score")]
        public double Popularity { get; init; }

        [VespaField(Name = "vector_embedding")]
        [VespaTensor("tensor<float>(x[2])")]
        public float[]? Embedding { get; init; }
    }

    [VespaDocument("other_article")]
    [VespaRankProfile("original", FirstPhase = "original_score")]
    [VespaRankFunction("original_score", "1")]
    private sealed record OtherArticle;

    [VespaDocument("invalid_bm25_article")]
    private sealed record InvalidBm25Article
    {
        [VespaField(EnableBm25 = true)]
        public string Title { get; init; } = "";
    }
}
