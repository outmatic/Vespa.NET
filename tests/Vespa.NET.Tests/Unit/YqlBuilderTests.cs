using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Query;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for the fluent YQL builder
/// </summary>
public class YqlBuilderTests
{
    // --- Select / From ---

    [Fact]
    public void Build_SelectStar_FromSources()
    {
        var yql = YqlBuilder.Select().From("music").Build();
        Assert.Equal("select * from music where true", yql);
    }

    [Fact]
    public void Build_SelectSpecificFields()
    {
        var yql = YqlBuilder.Select("title, artist").From("music").Build();
        Assert.Equal("select title, artist from music where true", yql);
    }

    [Fact]
    public void Build_NoFrom_UsesSourcesStar()
    {
        var yql = YqlBuilder.Select().Build();
        Assert.Equal("select * from sources * where true", yql);
    }

    // --- Where: Contains ---

    [Fact]
    public void Build_WhereContains_ProducesContainsPredicate()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("genre").Contains("rock"))
            .Build();

        Assert.Equal("select * from music where genre contains \"rock\"", yql);
    }

    [Fact]
    public void Build_WhereContains_EscapesQuotes()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").Contains("say \"hello\""))
            .Build();

        Assert.Contains(@"say \""hello\""", yql);
    }

    // --- Where: Matches ---

    [Fact]
    public void Build_WhereMatches_ProducesMatchesPredicate()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").Matches("rock.*"))
            .Build();

        Assert.Equal("select * from music where title matches \"rock.*\"", yql);
    }

    // --- Where: Comparisons ---

    [Fact]
    public void Build_WhereGreaterThan_ProducesCorrectOperator()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("year").GreaterThan(2000))
            .Build();

        Assert.Equal("select * from music where year > 2000", yql);
    }

    [Fact]
    public void Build_WhereLessThan()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("year").LessThan(1990))
            .Build();

        Assert.Equal("select * from music where year < 1990", yql);
    }

    [Fact]
    public void Build_WhereGreaterOrEqual()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("year").GreaterOrEqual(2000))
            .Build();

        Assert.Equal("select * from music where year >= 2000", yql);
    }

    [Fact]
    public void Build_WhereLessOrEqual()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("year").LessOrEqual(2000))
            .Build();

        Assert.Equal("select * from music where year <= 2000", yql);
    }

    [Fact]
    public void Build_WhereEqualTo_Int()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("year").EqualTo(2001))
            .Build();

        Assert.Equal("select * from music where year = 2001", yql);
    }

    [Fact]
    public void Build_WhereEqualTo_String()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("genre").EqualTo("rock"))
            .Build();

        Assert.Equal("select * from music where genre = \"rock\"", yql);
    }

    // --- Where: Range ---

    [Fact]
    public void Build_WhereRange_ProducesRangePredicate()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("year").Range(1990, 2010))
            .Build();

        Assert.Equal("select * from music where range(year, 1990, 2010)", yql);
    }

    // --- Where: NearestNeighbor ---

    [Fact]
    public void Build_WhereNearestNeighbor_ProducesCorrectSyntax()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.NearestNeighbor("embedding", "queryEmbedding"))
            .Build();

        Assert.Equal("select * from music where ({targetHits:10}nearestNeighbor(embedding, queryEmbedding))", yql);
    }

    [Fact]
    public void Build_WhereNearestNeighbor_DefaultTargetHits()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.NearestNeighbor("embedding", "q"))
            .Build();

        Assert.Contains("{targetHits:10}", yql);
    }

    // --- Where: True ---

    [Fact]
    public void Build_WhereTrue_SelectsAllDocuments()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.True())
            .Build();

        Assert.Equal("select * from music where true", yql);
    }

    // --- Multiple predicates (AND) ---

    [Fact]
    public void Build_MultipleFieldPredicates_ImplicitAnd()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w
                .Field("year").GreaterThan(2000)
                .Field("genre").Contains("rock"))
            .Build();

        Assert.Contains("year > 2000", yql);
        Assert.Contains("""genre contains "rock" """.Trim(), yql);
        Assert.Contains(" and ", yql);
    }

    // --- And sub-clause ---

    [Fact]
    public void Build_ExplicitAndSubClause_ProducesAndExpression()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w
                .Field("year").GreaterThan(2000)
                .And(w2 => w2.Field("genre").Contains("rock")))
            .Build();

        Assert.Contains("year > 2000", yql);
        Assert.Contains("rock", yql);
        Assert.Contains(" and ", yql);
    }

    // --- Or sub-clause ---

    [Fact]
    public void Build_OrSubClause_ProducesOrExpression()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w
                .Field("genre").Contains("rock")
                .Or(w2 => w2.Field("genre").Contains("jazz")))
            .Build();

        Assert.Contains("rock", yql);
        Assert.Contains("jazz", yql);
        Assert.Contains(" or ", yql);
    }

    // --- Order by ---

    [Fact]
    public void Build_OrderByDescending()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.True())
            .OrderBy("relevance", descending: true)
            .Build();

        Assert.Contains("order by relevance desc", yql);
    }

    [Fact]
    public void Build_OrderByAscending()
    {
        var yql = YqlBuilder.Select().From("music")
            .OrderBy("year")
            .Build();

        Assert.Contains("order by year asc", yql);
    }

    [Fact]
    public void Build_MultipleOrderBy_ProducesCommaSeparatedClauses()
    {
        var yql = YqlBuilder.Select().From("music")
            .OrderBy("relevance", descending: true)
            .OrderBy("year", descending: false)
            .Build();

        Assert.Contains("order by relevance desc, year asc", yql);
    }

    [Fact]
    public void Build_ThreeOrderByClauses_AllIncluded()
    {
        var yql = YqlBuilder.Select().From("music")
            .OrderBy("relevance", descending: true)
            .OrderBy("year", descending: false)
            .OrderBy("title", descending: true)
            .Build();

        Assert.Contains("order by relevance desc, year asc, title desc", yql);
    }

    [Fact]
    public void Build_TypedOrderBy_ResolvesFieldFromAttribute()
    {
        var yql = YqlBuilder<MusicModel>.Select()
            .OrderBy(m => m.ArtistName, descending: true)
            .OrderBy(m => m.Year)
            .Build();

        Assert.Contains("order by artist_name desc, year asc", yql);
    }

    // --- Limit / Offset ---

    [Fact]
    public void Build_WithLimit_AppendsLimit()
    {
        var yql = YqlBuilder.Select().From("music").Limit(10).Build();
        Assert.Contains("limit 10", yql);
    }

    [Fact]
    public void Build_WithOffset_AppendsOffset()
    {
        var yql = YqlBuilder.Select().From("music").Offset(5).Build();
        Assert.Contains("offset 5", yql);
    }

    [Fact]
    public void Build_WithLimitAndOffset()
    {
        var yql = YqlBuilder.Select().From("music").Limit(10).Offset(20).Build();
        Assert.Contains("limit 10", yql);
        Assert.Contains("offset 20", yql);
    }

    // --- Full complex query ---

    [Fact]
    public void Build_ComplexQuery_ProducesExpectedYql()
    {
        var yql = YqlBuilder
            .Select()
            .From("music")
            .Where(w => w
                .Field("year").GreaterThan(2000)
                .And(w2 => w2
                    .Field("genre").Contains("rock")
                    .Or(w3 => w3.Field("genre").Contains("jazz"))))
            .OrderBy("relevance", descending: true)
            .Limit(10)
            .Offset(0)
            .Build();

        Assert.StartsWith("select * from music where", yql);
        Assert.Contains("year > 2000", yql);
        Assert.Contains("rock", yql);
        Assert.Contains("jazz", yql);
        Assert.Contains("order by relevance desc", yql);
        Assert.Contains("limit 10", yql);
        Assert.Contains("offset 0", yql);
    }

    // --- ToString ---

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = YqlBuilder.Select().From("music").Where(w => w.True()).Limit(5);
        Assert.Equal(builder.Build(), builder.ToString());
    }

    // --- YqlExtensions ---

    [Fact]
    public void ToSearchRequest_SetsYqlProperty()
    {
        var request = YqlBuilder.Select().From("music").Where(w => w.True()).ToSearchRequest();

        Assert.NotNull(request);
        Assert.Equal("select * from music where true", request.Yql);
    }

    [Fact]
    public void WithYql_UpdatesExistingRequest()
    {
        var request = new VespaSearchRequest { Hits = 20 };
        var builder = YqlBuilder.Select().From("music");

        request.WithYql(builder);

        Assert.Equal("select * from music where true", request.Yql);
        Assert.Equal(20, request.Hits); // other properties preserved
    }

    // --- Double / float formatting ---

    [Fact]
    public void Build_DoubleValue_FormattedWithInvariantCulture()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("score").GreaterThan(0.5))
            .Build();

        Assert.Contains("0.5", yql);
        Assert.DoesNotContain("0,5", yql); // no locale-specific comma
    }

    // --- In ---

    [Fact]
    public void Build_InStrings_ProducesInPredicate()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("genre").In("rock", "jazz", "pop"))
            .Build();

        Assert.Contains("""genre in ("rock", "jazz", "pop")""", yql);
    }

    [Fact]
    public void Build_InIntegers_ProducesInPredicate()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("year").In(2000, 2010, 2020))
            .Build();

        Assert.Contains("year in (2000, 2010, 2020)", yql);
    }

    // --- Phrase ---

    [Fact]
    public void Build_Phrase_ProducesPhraseContains()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").Phrase("dark", "side", "moon"))
            .Build();

        Assert.Contains("""title contains phrase("dark", "side", "moon")""", yql);
    }

    // --- Fuzzy ---

    [Fact]
    public void Build_Fuzzy_ProducesFuzzyContains()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").Fuzzy("beatlez"))
            .Build();

        Assert.Contains("""title contains fuzzy("beatlez")""", yql);
    }

    [Fact]
    public void Build_Fuzzy_WithOptions_ProducesAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").Fuzzy("beatlez", maxEditDistance: 2, prefixLength: 1))
            .Build();

        Assert.Contains("maxEditDistance:2", yql);
        Assert.Contains("prefixLength:1", yql);
        Assert.Contains("fuzzy", yql);
        Assert.Contains("beatlez", yql);
    }

    // --- WeakAnd ---

    [Fact]
    public void Build_WeakAnd_ProducesWeakAndExpression()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.WeakAnd(s => s
                .Field("title").Contains("rock")
                .Field("body").Contains("guitar")))
            .Build();

        Assert.Contains("weakAnd(", yql);
        Assert.Contains("title contains", yql);
        Assert.Contains("body contains", yql);
    }

    [Fact]
    public void Build_WeakAnd_WithTargetHits_ProducesAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.WeakAnd(s => s
                .Field("default").Contains("term1")
                .Field("default").Contains("term2"),
                targetHits: 100))
            .Build();

        Assert.Equal(
            """"select * from music where ({targetHits:100}weakAnd(default contains "term1", default contains "term2"))"""",
            yql);
    }

    [Fact]
    public void Build_WeakAnd_WithoutTargetHits_NoAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.WeakAnd(s => s
                .Field("title").Contains("rock")
                .Field("body").Contains("guitar")))
            .Build();

        Assert.Equal(
            """"select * from music where weakAnd(title contains "rock", body contains "guitar")"""",
            yql);
    }

    [Fact]
    public void Build_WeakAnd_WithFuzzyPredicates_ProducesCorrectYql()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.WeakAnd(s => s
                .Field("default").Fuzzy("term1", maxEditDistance: 2, prefixLength: 2)
                .Field("default").Fuzzy("term2", maxEditDistance: 1, prefixLength: 2)))
            .Build();

        Assert.Equal(
            """"select * from music where weakAnd(default contains ({maxEditDistance:2,prefixLength:2}fuzzy("term1")), default contains ({maxEditDistance:1,prefixLength:2}fuzzy("term2")))"""",
            yql);
    }

    // --- Wand ---

    [Fact]
    public void Build_Wand_ProducesWandExpression()
    {
        var terms = new Dictionary<string, int> { ["rock"] = 2, ["jazz"] = 1 };
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Wand("tags", terms, targetHits: 10))
            .Build();

        Assert.Contains("wand(tags,", yql);
        Assert.Contains("targetHits:10", yql);
        Assert.Contains("\"rock\": 2", yql);
        Assert.Contains("\"jazz\": 1", yql);
    }

    // --- DotProduct ---

    [Fact]
    public void Build_DotProduct_ProducesDotProductExpression()
    {
        var terms = new Dictionary<string, int> { ["term1"] = 3, ["term2"] = 1 };
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.DotProduct("tags", terms))
            .Build();

        Assert.Contains("dotProduct(tags,", yql);
        Assert.Contains("\"term1\": 3", yql);
        Assert.Contains("\"term2\": 1", yql);
    }

    // --- GeoLocation ---

    [Fact]
    public void Build_GeoLocation_ProducesCorrectSyntax()
    {
        var yql = YqlBuilder.Select().From("venue")
            .Where(w => w.GeoLocation("location", 37.7749, -122.4194, 10.0))
            .Build();

        Assert.Contains("geoLocation(location, 37.7749, -122.4194, \"10km\")", yql);
    }

    [Fact]
    public void Build_GeoLocation_UsesInvariantCulture()
    {
        var yql = YqlBuilder.Select().From("venue")
            .Where(w => w.GeoLocation("location", 48.8566, 2.3522, 0.5))
            .Build();

        Assert.Contains("0.5km", yql);
        Assert.DoesNotContain("0,5km", yql);
    }

    [Fact]
    public void Build_GeoLocation_CombinedWithOtherPredicates()
    {
        var yql = YqlBuilder.Select().From("venue")
            .Where(w => w
                .Field("category").Contains("restaurant")
                .GeoLocation("location", 37.7749, -122.4194, 5.0))
            .Build();

        Assert.Contains("geoLocation(", yql);
        Assert.Contains("restaurant", yql);
    }

    [Fact]
    public void Build_TypedGeoLocation_ResolvesFieldFromAttribute()
    {
        var yql = YqlBuilder<VenueModel>.Select()
            .Where(w => w.GeoLocation(v => v.Location, 37.7749, -122.4194, 10.0))
            .Build();

        Assert.Contains("geoLocation(geo_location,", yql);
    }

    // --- UserQuery / UserInput ---

    [Fact]
    public void Build_UserQuery_ProducesCorrectSyntax()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.UserQuery("heavy metal"))
            .Build();

        Assert.Equal(""""select * from music where userQuery("heavy metal")"""", yql);
    }

    [Fact]
    public void Build_UserQuery_EscapesQuotes()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.UserQuery("say \"hello\""))
            .Build();

        Assert.Contains(@"userQuery(""say \""hello\"""")", yql);
    }

    [Fact]
    public void Build_UserInput_ProducesCorrectSyntax()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.UserInput("query"))
            .Build();

        Assert.Equal("select * from music where userInput(@query)", yql);
    }

    [Fact]
    public void Build_UserInput_CombinedWithOtherPredicates()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w
                .Field("genre").EqualTo("rock")
                .UserInput("userText"))
            .Build();

        Assert.Contains("userInput(@userText)", yql);
        Assert.Contains("genre = \"rock\"", yql);
    }

    [Fact]
    public void Build_TypedUserQuery_Works()
    {
        var yql = YqlBuilder<MusicModel>.Select()
            .Where(w => w.UserQuery("jazz classics"))
            .Build();

        Assert.Contains("""userQuery("jazz classics")""", yql);
    }

    [Fact]
    public void Build_TypedUserInput_Works()
    {
        var yql = YqlBuilder<MusicModel>.Select()
            .Where(w => w.UserInput("myParam"))
            .Build();

        Assert.Contains("userInput(@myParam)", yql);
    }

    // --- Near / ONear ---

    [Fact]
    public void Build_Near_ProducesNearContains()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").Near(["dark", "side"]))
            .Build();

        Assert.Contains("""title contains near("dark", "side")""", yql);
    }

    [Fact]
    public void Build_Near_WithDistance_ProducesAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").Near(["dark", "side"], distance: 5))
            .Build();

        Assert.Contains("""title contains ({distance: 5}near("dark", "side"))""", yql);
    }

    [Fact]
    public void Build_ONear_ProducesOrderedNear()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").ONear(["dark", "side"]))
            .Build();

        Assert.Contains("""title contains onear("dark", "side")""", yql);
    }

    [Fact]
    public void Build_ONear_WithDistance_ProducesAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").ONear(["dark", "side", "moon"], distance: 3))
            .Build();

        Assert.Contains("""{distance: 3}onear("dark", "side", "moon")""", yql);
    }

    // --- Equiv ---

    [Fact]
    public void Build_Equiv_ProducesEquivContains()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").Equiv("car", "automobile", "vehicle"))
            .Build();

        Assert.Contains("""title contains equiv("car", "automobile", "vehicle")""", yql);
    }

    // --- SameElement ---

    [Fact]
    public void Build_SameElement_ProducesSameElementPredicate()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("persons").SameElement(se =>
                se.Contains("first_name", "Joe")
                  .Field("last_name", "=", "Smith")))
            .Build();

        Assert.Contains("""persons contains sameElement(first_name contains "Joe", last_name = "Smith")""", yql);
    }

    // --- WeightedSet ---

    [Fact]
    public void Build_WeightedSet_ProducesCorrectSyntax()
    {
        var tokens = new Dictionary<string, int> { ["rock"] = 3, ["pop"] = 1 };
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.WeightedSet("tags", tokens))
            .Build();

        Assert.Contains("weightedSet(tags,", yql);
        Assert.Contains("\"rock\": 3", yql);
        Assert.Contains("\"pop\": 1", yql);
    }

    // --- Rank ---

    [Fact]
    public void Build_Rank_ProducesRankExpression()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Rank(
                match => match.Field("title").Contains("rock"),
                rank1 => rank1.Field("body").Contains("guitar"),
                rank2 => rank2.Field("tags").Contains("classic")))
            .Build();

        Assert.Contains("rank(", yql);
        Assert.Contains("""title contains "rock" """.Trim(), yql);
        Assert.Contains("""body contains "guitar" """.Trim(), yql);
        Assert.Contains("""tags contains "classic" """.Trim(), yql);
    }

    // --- NonEmpty ---

    [Fact]
    public void Build_NonEmpty_WrapsExpression()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.NonEmpty(inner => inner.UserInput("query")))
            .Build();

        Assert.Contains("nonEmpty(userInput(@query))", yql);
    }

    // --- GeoBoundingBox ---

    [Fact]
    public void Build_GeoBoundingBox_ProducesCorrectSyntax()
    {
        var yql = YqlBuilder.Select().From("venue")
            .Where(w => w.GeoBoundingBox("location", 37.0, -123.0, 38.0, -122.0))
            .Build();

        Assert.Contains("southWest:", yql);
        Assert.Contains("northEast:", yql);
        Assert.Contains("geoBoundingBox(location)", yql);
        Assert.Contains("lat: 37", yql);
        Assert.Contains("lng: -123", yql);
        Assert.Contains("lat: 38", yql);
        Assert.Contains("lng: -122", yql);
    }

    // --- False ---

    [Fact]
    public void Build_WhereFalse_MatchesNoDocuments()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.False())
            .Build();

        Assert.Equal("select * from music where false", yql);
    }

    // --- NearestNeighbor Annotations ---

    [Fact]
    public void Build_NearestNeighbor_WithLabel()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.NearestNeighbor("embedding", "q", targetHits: 10, label: "myLabel"))
            .Build();

        Assert.Contains("""label:"myLabel" """.Trim(), yql);
        Assert.Contains("targetHits:10", yql);
    }

    [Fact]
    public void Build_NearestNeighbor_WithApproximate()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.NearestNeighbor("embedding", "q", approximate: false))
            .Build();

        Assert.Contains("approximate:false", yql);
    }

    [Fact]
    public void Build_NearestNeighbor_WithDistanceThreshold()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.NearestNeighbor("embedding", "q", distanceThreshold: 0.5))
            .Build();

        Assert.Contains("distanceThreshold:0.5", yql);
    }

    [Fact]
    public void Build_NearestNeighbor_WithHnswExploreAdditionalHits()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.NearestNeighbor("embedding", "q", hnswExploreAdditionalHits: 200))
            .Build();

        Assert.Contains("hnsw.exploreAdditionalHits:200", yql);
    }

    [Fact]
    public void Build_NearestNeighbor_AllAnnotations()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.NearestNeighbor("embedding", "q",
                targetHits: 50, label: "nn", approximate: true,
                distanceThreshold: 1.5, hnswExploreAdditionalHits: 100))
            .Build();

        Assert.Contains("targetHits:50", yql);
        Assert.Contains("""label:"nn" """.Trim(), yql);
        Assert.Contains("approximate:true", yql);
        Assert.Contains("distanceThreshold:1.5", yql);
        Assert.Contains("hnsw.exploreAdditionalHits:100", yql);
        Assert.Contains("nearestNeighbor(embedding, q)", yql);
    }

    // --- Wand scoreThreshold ---

    [Fact]
    public void Build_Wand_WithScoreThreshold()
    {
        var terms = new Dictionary<string, int> { ["rock"] = 2 };
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Wand("tags", terms, targetHits: 10, scoreThreshold: 0.5))
            .Build();

        Assert.Contains("scoreThreshold:0.5", yql);
        Assert.Contains("targetHits:10", yql);
    }

    // --- UserInput annotations ---

    [Fact]
    public void Build_UserInput_WithGrammar()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.UserInput("query", grammar: "weakAnd"))
            .Build();

        Assert.Contains("""{grammar: "weakAnd"}userInput(@query)""", yql);
    }

    [Fact]
    public void Build_UserInput_WithDefaultIndex()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.UserInput("query", defaultIndex: "title"))
            .Build();

        Assert.Contains("""defaultIndex: "title" """.Trim(), yql);
        Assert.Contains("userInput(@query)", yql);
    }

    [Fact]
    public void Build_UserInput_WithAllAnnotations()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.UserInput("query", grammar: "all", defaultIndex: "title", language: "en", allowEmpty: true))
            .Build();

        Assert.Contains("""grammar: "all" """.Trim(), yql);
        Assert.Contains("""defaultIndex: "title" """.Trim(), yql);
        Assert.Contains("""language: "en" """.Trim(), yql);
        Assert.Contains("allowEmpty: true", yql);
        Assert.Contains("userInput(@query)", yql);
    }

    // --- Predicate ---

    [Fact]
    public void Build_Predicate_ProducesPredicateSyntax()
    {
        var attrs = new Dictionary<string, string> { ["gender"] = "female", ["sport"] = "tennis" };
        var yql = YqlBuilder.Select().From("ad")
            .Where(w => w.Predicate("target", attrs))
            .Build();

        Assert.Contains("predicate(target,", yql);
        Assert.Contains("\"gender\": \"female\"", yql);
        Assert.Contains("\"sport\": \"tennis\"", yql);
    }

    [Fact]
    public void Build_Predicate_WithRangeAttributes()
    {
        var attrs = new Dictionary<string, string> { ["gender"] = "male" };
        var ranges = new Dictionary<string, long> { ["age"] = 25 };
        var yql = YqlBuilder.Select().From("ad")
            .Where(w => w.Predicate("target", attrs, ranges))
            .Build();

        Assert.Contains("predicate(target,", yql);
        Assert.Contains("\"age\": 25L", yql);
    }

    // --- Uri ---

    [Fact]
    public void Build_Uri_ProducesUriContains()
    {
        var yql = YqlBuilder.Select().From("web")
            .Where(w => w.Field("url").Uri("example.com"))
            .Build();

        Assert.Contains("""url contains (uri("example.com"))""", yql);
    }

    [Fact]
    public void Build_Uri_WithAnchors()
    {
        var yql = YqlBuilder.Select().From("web")
            .Where(w => w.Field("url").Uri("example.com", startAnchor: true, endAnchor: false))
            .Build();

        Assert.Contains("startAnchor: true", yql);
        Assert.Contains("endAnchor: false", yql);
        Assert.Contains("""uri("example.com")""", yql);
    }

    // --- Typed builders for new operators ---

    [Fact]
    public void Build_TypedNearestNeighbor_WithAnnotations()
    {
        var yql = YqlBuilder<MusicModel>.Select()
            .Where(w => w.NearestNeighbor("embedding", "q",
                targetHits: 20, label: "myNN", approximate: true))
            .Build();

        Assert.Contains("targetHits:20", yql);
        Assert.Contains("""label:"myNN" """.Trim(), yql);
        Assert.Contains("approximate:true", yql);
    }

    [Fact]
    public void Build_TypedFalse_Works()
    {
        var yql = YqlBuilder<MusicModel>.Select()
            .Where(w => w.False())
            .Build();

        Assert.Equal("select * from music where false", yql);
    }

    [Fact]
    public void Build_TypedGeoBoundingBox_ResolvesField()
    {
        var yql = YqlBuilder<VenueModel>.Select()
            .Where(w => w.GeoBoundingBox(v => v.Location, 37.0, -123.0, 38.0, -122.0))
            .Build();

        Assert.Contains("geoBoundingBox(geo_location)", yql);
    }

    [Fact]
    public void Build_TypedUserInput_WithGrammar()
    {
        var yql = YqlBuilder<MusicModel>.Select()
            .Where(w => w.UserInput("text", grammar: "weakAnd"))
            .Build();

        Assert.Contains("""{grammar: "weakAnd"}userInput(@text)""", yql);
    }

    // --- M13: filter annotation ---

    [Fact]
    public void Build_ContainsAnnotated_Filter_ProducesAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("genre").ContainsAnnotated("rock", filter: true))
            .Build();

        Assert.Contains("""genre contains ({filter: true}"rock")""", yql);
    }

    // --- M13: connectivity annotation ---

    [Fact]
    public void Build_ContainsAnnotated_TermId_ProducesIdAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").ContainsAnnotated("rock", termId: 1))
            .Build();

        Assert.Contains("""title contains ({id: 1}"rock")""", yql);
    }

    [Fact]
    public void Build_ContainsAnnotated_Connectivity_ProducesConnectivityAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").ContainsAnnotated("roll", termId: 1, connectivityWeight: 0.8))
            .Build();

        Assert.Contains("""connectivity: {id: 1, weight: 0.8}""", yql);
    }

    // --- M13: stem/normalizeCase/accentDrop/usePositionData annotations ---

    [Fact]
    public void Build_ContainsAnnotated_Stem_ProducesAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").ContainsAnnotated("running", stem: false))
            .Build();

        Assert.Contains("""stem: false""", yql);
    }

    [Fact]
    public void Build_ContainsAnnotated_NormalizeCase_ProducesAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").ContainsAnnotated("Rock", normalizeCase: false))
            .Build();

        Assert.Contains("""normalizeCase: false""", yql);
    }

    [Fact]
    public void Build_ContainsAnnotated_AccentDrop_ProducesAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").ContainsAnnotated("café", accentDrop: false))
            .Build();

        Assert.Contains("""accentDrop: false""", yql);
    }

    [Fact]
    public void Build_ContainsAnnotated_UsePositionData_ProducesAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").ContainsAnnotated("rock", usePositionData: false))
            .Build();

        Assert.Contains("""usePositionData: false""", yql);
    }

    [Fact]
    public void Build_ContainsAnnotated_MultipleAnnotations()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").ContainsAnnotated("rock", filter: true, stem: false, normalizeCase: false))
            .Build();

        Assert.Contains("filter: true", yql);
        Assert.Contains("stem: false", yql);
        Assert.Contains("normalizeCase: false", yql);
    }

    // --- M13: hitLimit annotation on range ---

    [Fact]
    public void Build_Range_WithHitLimit_ProducesAnnotation()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("year").Range(2000, 2020, hitLimit: 1000))
            .Build();

        Assert.Contains("{hitLimit:1000}range(year,", yql);
    }

    // --- M13: bounds annotation on range ---

    [Fact]
    public void Build_Range_WithBounds_OpenOpen()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("price").Range(10.0, 100.0, bounds: "open"))
            .Build();

        Assert.Contains("""bounds:"open" """.Trim(), yql);
        Assert.Contains("range(price,", yql);
    }

    [Fact]
    public void Build_Range_WithHitLimitAndBounds()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("year").Range(2000, 2020, hitLimit: 500, bounds: "leftOpen"))
            .Build();

        Assert.Contains("hitLimit:500", yql);
        Assert.Contains("""bounds:"leftOpen" """.Trim(), yql);
    }

    // --- M13: text() operator ---

    [Fact]
    public void Build_Text_ProducesTextContains()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").Text("running shoes"))
            .Build();

        Assert.Contains("""title contains text("running shoes")""", yql);
    }

    // --- M13: contains without annotations still works ---

    [Fact]
    public void Build_Contains_WithoutAnnotations_UnchangedFormat()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.Field("title").Contains("rock"))
            .Build();

        Assert.Contains("""title contains "rock" """.Trim(), yql);
        Assert.DoesNotContain("{", yql.Split("where")[1].Split(";")[0].Replace("\"rock\"", ""));
    }

    // --- Helpers ---

    [VespaDocument("music")]
    private record MusicModel
    {
        [VespaField(Name = "artist_name")]
        public string ArtistName { get; init; } = "";

        public int Year { get; init; }
    }

    [VespaDocument("venue")]
    private record VenueModel
    {
        [VespaField(Name = "geo_location")]
        public object Location { get; init; } = default!;
    }

    // --- HybridSearch ---

    [Fact]
    public void Build_HybridSearch_ProducesRankPattern()
    {
        var yql = YqlBuilder.Select().From("memory")
            .Where(w => w.HybridSearch("embedding", "q", "userQuery", targetHits: 30))
            .Build();

        Assert.Equal(
            "select * from memory where rank(({targetHits:30}nearestNeighbor(embedding, q)), userInput(@userQuery))",
            yql);
    }

    [Fact]
    public void Build_HybridSearch_WithAnnotations()
    {
        var yql = YqlBuilder.Select().From("memory")
            .Where(w => w.HybridSearch("embedding", "q", "userQuery",
                targetHits: 50, label: "emb", approximate: true, distanceThreshold: 0.5))
            .Build();

        Assert.Contains("rank(", yql);
        Assert.Contains(", userInput(@userQuery))", yql);
        Assert.Contains("label:\"emb\"", yql);
        Assert.Contains("approximate:true", yql);
        Assert.Contains("distanceThreshold:0.5", yql);
        Assert.Contains("targetHits:50", yql);
    }

    [Fact]
    public void Build_TypedHybridSearch_WithLambda()
    {
        var yql = YqlBuilder<EmbeddingModel>.Select()
            .Where(w => w.HybridSearch(m => m.Embedding, "q", "userQuery", targetHits: 30))
            .Build();

        Assert.Contains("rank(({targetHits:30}nearestNeighbor(vector_embedding, q)), userInput(@userQuery))", yql);
    }

    [Fact]
    public void Build_TypedHybridSearch_StringField()
    {
        var yql = YqlBuilder<EmbeddingModel>.Select()
            .Where(w => w.HybridSearch("embedding", "q", "userQuery", targetHits: 30))
            .Build();

        Assert.Contains("rank(({targetHits:30}nearestNeighbor(embedding, q)), userInput(@userQuery))", yql);
    }

    // --- AnyOf ---

    [Fact]
    public void Build_AnyOf_ProducesFlatOr()
    {
        var yql = YqlBuilder.Select().From("doc")
            .Where(w => w.AnyOf(
                p => p.Field("pinned").EqualTo(1),
                p => p.Field("expires_at").EqualTo(0),
                p => p.Field("expires_at").GreaterThan(1000)))
            .Build();

        Assert.Equal(
            "select * from doc where pinned = 1 or expires_at = 0 or expires_at > 1000",
            yql);
    }

    [Fact]
    public void Build_AnyOf_CombinedWithAndPredicates()
    {
        var yql = YqlBuilder.Select().From("doc")
            .Where(w =>
            {
                w.Field("scope").Contains("Session");
                w.And(a => a.AnyOf(
                    p => p.Field("pinned").EqualTo(1),
                    p => p.Field("expires_at").EqualTo(0)));
            })
            .Build();

        Assert.Contains("""scope contains "Session" """.Trim(), yql);
        Assert.Contains("pinned = 1 or expires_at = 0", yql);
    }

    [Fact]
    public void Build_TypedAnyOf_Works()
    {
        var yql = YqlBuilder<EmbeddingModel>.Select()
            .Where(w => w.AnyOf(
                p => p.Field("pinned").EqualTo(1),
                p => p.Field("active").EqualTo(1)))
            .Build();

        Assert.Contains("pinned = 1 or active = 1", yql);
    }

    // --- Validation guards ---

    [Fact]
    public void AnyOf_WithZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            YqlBuilder.Select().From("doc")
                .Where(w => w.AnyOf())
                .Build());
    }

    [Fact]
    public void AnyOf_WithSinglePredicate_UnwrapsDirectly()
    {
        var yql = YqlBuilder.Select().From("doc")
            .Where(w => w.AnyOf(p => p.Field("a").EqualTo(1)))
            .Build();

        Assert.Equal("select * from doc where a = 1", yql);
    }

    [Fact]
    public void Rank_WithZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            YqlBuilder.Select().From("doc")
                .Where(w => w.Rank())
                .Build());
    }

    [Fact]
    public void Rank_WithSingleClause_UnwrapsDirectly()
    {
        var yql = YqlBuilder.Select().From("doc")
            .Where(w => w.Rank(match => match.Field("a").EqualTo(1)))
            .Build();

        Assert.Equal("select * from doc where a = 1", yql);
    }

    [Fact]
    public void Contains_WithEmptyString_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            YqlBuilder.Select().From("doc")
                .Where(w => w.Field("title").Contains(""))
                .Build());
    }

    [Fact]
    public void Contains_WithWhitespace_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            YqlBuilder.Select().From("doc")
                .Where(w => w.Field("title").Contains("   "))
                .Build());
    }

    [Fact]
    public void Or_WithMultiplePredicatesInCallback_ProducesOr()
    {
        var yql = YqlBuilder.Select().From("doc")
            .Where(w => w.Field("a").EqualTo(1)
                .Or(or =>
                {
                    or.Field("b").EqualTo(2);
                    or.Field("c").EqualTo(3);
                }))
            .Build();

        Assert.Equal("select * from doc where a = 1 or b = 2 or c = 3", yql);
    }

    [Fact]
    public void Or_OnEmptyClause_WithMultiplePredicates_ProducesOr()
    {
        var yql = YqlBuilder.Select().From("doc")
            .Where(w => w.Or(or =>
            {
                or.Field("pinned").EqualTo(1);
                or.Field("expires_at").EqualTo(0);
                or.Field("expires_at").GreaterThan(1000);
            }))
            .Build();

        Assert.Equal("select * from doc where pinned = 1 or expires_at = 0 or expires_at > 1000", yql);
    }

    [Fact]
    public void WeakAnd_WithEmptyCallback_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            YqlBuilder.Select().From("doc")
                .Where(w => w.WeakAnd(_ => { }))
                .Build());
    }

    [Theory]
    [InlineData("", "q", "param")]
    [InlineData("field", "", "param")]
    [InlineData("field", "q", "")]
    public void HybridSearch_WithEmptyParam_Throws(string field, string tensor, string param)
    {
        Assert.Throws<ArgumentException>(() =>
            YqlBuilder.Select().From("doc")
                .Where(w => w.HybridSearch(field, tensor, param))
                .Build());
    }

    [VespaDocument("embedding_doc")]
    private record EmbeddingModel
    {
        [VespaField(Name = "vector_embedding")]
        public object Embedding { get; init; } = default!;
    }
}
