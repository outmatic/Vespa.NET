using Vespa.Models.Attributes;
using Vespa.Query;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class YqlNestedCompositionTests
{
    [Fact]
    public void TypedNestedCallbacks_PreserveMappedFieldsEscapingAndUserQueryBinding()
    {
        const string userText = "one \"quoted\" term";
        var request = YqlBuilder<CatalogItem>.Select()
            .Where(where => where.And(group => group.AnyOf(
                first => first.Rank(
                    match => match.Or(term => term.Field(item => item.Category).Contains("a\"b\\c")),
                    ranking => ranking.WeakAnd(weak => weak.Field(item => item.Category).Contains("ranking"), totalTargetHits: 7)),
                second => second.Not(not => not.NonEmpty(nonEmpty => nonEmpty.UserQuery(userText))))))
            .ToSearchRequest();

        Assert.Equal(
            """select * from catalog where rank(category_name contains "a\"b\\c", ({totalTargetHits:7}weakAnd(category_name contains "ranking"))) or !(nonEmpty(userQuery()))""",
            request.Yql);
        Assert.Equal(userText, request.ModelQueryString);
        Assert.DoesNotContain(userText, request.Yql);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TypedSingleCallback_PreservesMutationOfExistingClause(bool useRank)
    {
        var calls = 0;
        var request = YqlBuilder<CatalogItem>.Select()
            .Where(where =>
            {
                where.UserQuery("initial text");
                Action<TypedYqlWhereClause<CatalogItem>> configure = clause =>
                {
                    calls++;
                    clause.UserQuery("replacement text");
                    clause.Field(item => item.Price).GreaterThan(10);
                };
                if (useRank)
                    where.Rank(configure);
                else
                    where.AnyOf(configure);
            })
            .ToSearchRequest();

        Assert.Equal("select * from catalog where userQuery() and userQuery() and price > 10", request.Yql);
        Assert.Equal("replacement text", request.ModelQueryString);
        Assert.Equal(1, calls);
    }

    [VespaDocument("catalog")]
    private sealed record CatalogItem
    {
        [VespaField(Name = "category_name")]
        public string Category { get; init; } = "";

        [VespaField(Name = "price")]
        public double Price { get; init; }
    }
}
