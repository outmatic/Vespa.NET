using System.Text.Json;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Query;
using Xunit;

namespace Vespa.Tests.Unit;

public class SimpleQueryBuilderTests
{
    [Fact]
    public void Build_UsesSignsAndMappedFieldNames()
    {
        var query = new SimpleQueryBuilder()
            .Word("cafe\u0301", SimpleQueryOccurrence.Required)
            .Phrase<Article>(article => article.Title, "  chambre\tbleue ", SimpleQueryOccurrence.Excluded)
            .Word("2026", field: "year");

        Assert.Equal("+cafe\u0301 -article_title:\"chambre bleue\" year:2026", query.Build());
    }

    [Fact]
    public void Build_RejectsSyntaxInWordsAndPhrasesBeforeAddingATerm()
    {
        var query = new SimpleQueryBuilder().Word("keep");
        Assert.Throws<ArgumentException>("text", () => query.Word("+extra"));
        Assert.Throws<ArgumentException>("text", () => query.Word("a b"));
        Assert.Throws<ArgumentException>("text", () => query.Phrase("word\" -other"));
        Assert.Throws<ArgumentException>("text", () => query.Phrase(" "));
        Assert.Throws<ArgumentException>("field", () => query.Word("books", field: "category:"));
        Assert.Throws<ArgumentOutOfRangeException>("occurrence", () => query.Word("books", (SimpleQueryOccurrence)42));
        Assert.Equal("keep", query.Build());
        Assert.Throws<InvalidOperationException>(() => new SimpleQueryBuilder().Build());
    }

    [Fact]
    public void Request_UsesTheNativeParametersWithoutEmbeddingTermsInYql()
    {
        var filter = new SimpleQueryBuilder().Word("books", SimpleQueryOccurrence.Required, field: "category");
        var builder = YqlBuilder<Article>.Select().Where(w => w.UserQuery());
        var request = builder.Limit(5).ToSearchRequest()
            .WithModelFilter(filter)
            .WithSimpleQuery(new SimpleQueryBuilder().Word("vespa", SimpleQueryOccurrence.Required));
        filter.Word("ignored");

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(request));
        Assert.Equal("select * from indexed_article where userQuery()", json.RootElement.GetProperty("yql").GetString());
        Assert.Equal("+category:books", json.RootElement.GetProperty("model.filter").GetString());
        Assert.Equal("+vespa", json.RootElement.GetProperty("model.queryString").GetString());
        Assert.Equal("any", json.RootElement.GetProperty("model.type").GetString());
        Assert.Equal(5, request.Hits);
    }

    [Fact]
    public void UserQuery_WithoutArgumentUsesExistingTextAndParserType()
    {
        var request = new VespaSearchRequest().WithModelQueryText(" title:vespa -java \"query languages\" ")
            .WithYql(YqlBuilder.Select().From("indexed_article").Where(w => w.UserQuery()));
        Assert.Equal("select * from indexed_article where userQuery()", request.Yql);
        Assert.Equal(" title:vespa -java \"query languages\" ", request.ModelQueryString);
        Assert.Equal("tokenize", request.ModelType);

        var raw = YqlBuilder.Select().From("indexed_article").Where(w => w.UserQuery("+title:vespa -java")).ToSearchRequest();
        Assert.Equal("+title:vespa -java", raw.ModelQueryString);
        Assert.Null(raw.ModelType);
    }

    [Fact]
    public void Recall_UsesSignedWordsAndPhrasesWithoutChangingTheModelQuery()
    {
        var request = new VespaSearchRequest { ModelQueryString = "search", ModelType = "tokenize", Offset = 3 };
        var recall = new SimpleQueryBuilder()
            .Phrase<Article>(article => article.Title, "space opera", SimpleQueryOccurrence.Required)
            .Word("outdated", SimpleQueryOccurrence.Excluded);

        var updated = request.WithRecall(recall);
        Assert.Null(request.Recall);
        Assert.Equal("+article_title:\"space opera\" -outdated", updated.Recall);
        Assert.Equal("search", updated.ModelQueryString);
        Assert.Equal("tokenize", updated.ModelType);
        Assert.Equal(3, updated.Offset);

        var replaced = updated.WithRecall(new SimpleQueryBuilder().Word("outdated", SimpleQueryOccurrence.Excluded));
        Assert.Equal("-outdated", replaced.Recall);
    }

    [Fact]
    public void Recall_RejectsAnEmptyBuilderOrAnUnsignedTermBeforeChangingTheRequest()
    {
        var request = new VespaSearchRequest { Recall = "+books" };
        var recall = new SimpleQueryBuilder().Word("books", SimpleQueryOccurrence.Required).Phrase("unsigned words");

        Assert.Throws<InvalidOperationException>(() => request.WithRecall(new SimpleQueryBuilder()));
        Assert.Throws<InvalidOperationException>(() => request.WithRecall(recall));
        Assert.Equal("+books", request.Recall);
    }

    [VespaDocument("indexed_article")]
    private sealed record Article
    {
        [VespaField(Name = "article_title")]
        public string Title { get; init; } = "";
    }
}
