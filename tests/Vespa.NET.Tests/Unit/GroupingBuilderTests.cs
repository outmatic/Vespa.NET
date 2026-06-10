using System.Net;
using System.Text.Json;
using Vespa;
using Vespa.Models;
using Vespa.Query;
using Vespa.Search;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for GroupingBuilder, GroupingAgg, YqlBuilder.GroupBy, and GroupByAsync response parsing
/// </summary>
public class GroupingBuilderTests : IDisposable
{
    private readonly MockHttpMessageHandler _mockHandler;
    private readonly HttpClient _httpClient;
    private readonly VespaClientOptions _options;
    private readonly SearchOperations _searchOps;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public GroupingBuilderTests()
    {
        _mockHandler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_mockHandler) { BaseAddress = new Uri("http://localhost:8080") };
        _options = new VespaClientOptions { Endpoint = "http://localhost:8080", DefaultNamespace = "test" };
        _searchOps = new SearchOperations(_httpClient, _options);
    }

    public void Dispose() => _httpClient.Dispose();

    // --- GroupingAgg factories ---

    [Fact] public void GroupingAgg_Count_ReturnsCountExpr() => Assert.Equal("count()", GroupingAgg.Count());
    [Fact] public void GroupingAgg_Sum_ReturnsExpr() => Assert.Equal("sum(price)", GroupingAgg.Sum("price"));
    [Fact] public void GroupingAgg_Avg_ReturnsExpr() => Assert.Equal("avg(year)", GroupingAgg.Avg("year"));
    [Fact] public void GroupingAgg_Min_ReturnsExpr() => Assert.Equal("min(year)", GroupingAgg.Min("year"));
    [Fact] public void GroupingAgg_Max_ReturnsExpr() => Assert.Equal("max(year)", GroupingAgg.Max("year"));
    [Fact] public void GroupingAgg_StdDev_ReturnsExpr() => Assert.Equal("stddev(score)", GroupingAgg.StdDev("score"));
    [Fact] public void GroupingAgg_Xor_ReturnsExpr() => Assert.Equal("xor(id)", GroupingAgg.Xor("id"));
    [Fact] public void GroupingAgg_Relevance_ReturnsRelevanceExpr() => Assert.Equal("relevance()", GroupingAgg.Relevance());
    [Fact] public void GroupingAgg_Summary_NoArgs_ReturnsDefaultSummary() => Assert.Equal("summary()", GroupingAgg.Summary());
    [Fact] public void GroupingAgg_Summary_WithClass_ReturnsSummaryWithClass() => Assert.Equal("summary(compact)", GroupingAgg.Summary("compact"));

    // --- Grouping continuation token ---

    [Fact]
    public void GroupingContinuation_SetOnSearchRequest_IncludedInSerialization()
    {
        var request = new VespaSearchRequest
        {
            Yql = "select * from music;",
            GroupingContinuation = "BCBCBCBEBGBCBKCBACBKCCK"
        };

        var json = JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        Assert.Contains("\"grouping.continuation\"", json);
        Assert.Contains("BCBCBCBEBGBCBKCBACBKCCK", json);
    }

    // --- GroupingBuilder: basic all() ---

    [Fact]
    public void Build_AllWithGroup_ProducesAllGroupExpr()
    {
        var g = GroupingBuilder.All().Group("genre").Build();
        Assert.Equal("all(group(genre))", g);
    }

    [Fact]
    public void Build_AllWithGroupAndMax()
    {
        var g = GroupingBuilder.All().Group("genre").Max(10).Build();
        Assert.Equal("all(group(genre) max(10))", g);
    }

    [Fact]
    public void Build_AllWithGroupMaxPrecision()
    {
        var g = GroupingBuilder.All().Group("genre").Max(10).Precision(100).Build();
        Assert.Equal("all(group(genre) max(10) precision(100))", g);
    }

    [Fact]
    public void Build_AllWithOrderByDescending()
    {
        var g = GroupingBuilder.All()
            .Group("genre")
            .Max(10)
            .OrderByDescending(GroupingAgg.Count())
            .Build();
        Assert.Equal("all(group(genre) max(10) order(-count()))", g);
    }

    [Fact]
    public void Build_AllWithOrderByAscending()
    {
        var g = GroupingBuilder.All()
            .Group("year")
            .OrderByAscending(GroupingAgg.Avg("price"))
            .Build();
        Assert.Equal("all(group(year) order(+avg(price)))", g);
    }

    // --- EachGroupingBuilder ---

    [Fact]
    public void Build_AllWithEachAndSingleOutput()
    {
        var g = GroupingBuilder.All()
            .Group("genre")
            .Each(e => e.Output(GroupingAgg.Count()))
            .Build();
        Assert.Equal("all(group(genre) each(output(count())))", g);
    }

    [Fact]
    public void Build_AllWithEachAndMultipleOutputs()
    {
        var g = GroupingBuilder.All()
            .Group("genre")
            .Max(10)
            .Each(e => e.Output(GroupingAgg.Count(), GroupingAgg.Avg("year")))
            .Build();
        Assert.Equal("all(group(genre) max(10) each(output(count(), avg(year))))", g);
    }

    [Fact]
    public void Build_AllWithOrderAndEach()
    {
        var g = GroupingBuilder.All()
            .Group("genre")
            .Max(10)
            .OrderByDescending(GroupingAgg.Count())
            .Each(e => e.Output(GroupingAgg.Count(), GroupingAgg.Avg("year")))
            .Build();
        Assert.Equal("all(group(genre) max(10) order(-count()) each(output(count(), avg(year))))", g);
    }

    // --- Nested grouping ---

    [Fact]
    public void Build_NestedGrouping_ProducesNestedExpr()
    {
        var g = GroupingBuilder.All()
            .Group("genre")
            .Max(5)
            .Each(e => e
                .Output(GroupingAgg.Count())
                .SubGroup(GroupingBuilder.All()
                    .Group("year")
                    .Max(3)
                    .Each(e2 => e2.Output(GroupingAgg.Count()))))
            .Build();

        Assert.Equal(
            "all(group(genre) max(5) each(output(count()) all(group(year) max(3) each(output(count())))))",
            g);
    }

    // --- ToString ---

    [Fact]
    public void ToString_ReturnsSameAsBuild()
    {
        var builder = GroupingBuilder.All().Group("genre").Max(10);
        Assert.Equal(builder.Build(), builder.ToString());
    }

    // --- YqlBuilder.GroupBy integration ---

    [Fact]
    public void YqlBuilder_GroupBy_AppendsAfterWhere()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.True())
            .GroupBy(GroupingBuilder.All()
                .Group("genre")
                .Max(10)
                .Each(e => e.Output(GroupingAgg.Count())))
            .Build();

        Assert.Equal("select * from music where true | all(group(genre) max(10) each(output(count())))", yql);
    }

    [Fact]
    public void YqlBuilder_GroupBy_WorksWithLimitOffset()
    {
        var yql = YqlBuilder.Select().From("music")
            .Where(w => w.True())
            .Limit(0)
            .GroupBy(GroupingBuilder.All().Group("genre").Each(e => e.Output(GroupingAgg.Count())))
            .Build();

        Assert.Contains(" | all(", yql);
        Assert.Contains("limit 0", yql);
    }

    [Fact]
    public void YqlBuilder_WithoutGroupBy_NoGroupingInOutput()
    {
        var yql = YqlBuilder.Select().From("music").Where(w => w.True()).Build();
        Assert.DoesNotContain(" | ", yql);
    }

    // --- GroupByAsync response parsing ---

    [Fact]
    public async Task GroupByAsync_ParsesSingleGroupList()
    {
        var json = """
        {
          "root": {
            "id": "toplevel",
            "relevance": 1.0,
            "fields": {"totalCount": 500},
            "children": [
              {
                "id": "group:root:0",
                "relevance": 1.0,
                "children": [
                  {
                    "id": "grouplist:genre",
                    "label": "genre",
                    "children": [
                      {
                        "id": "group:string:rock",
                        "value": "rock",
                        "relevance": 1.0,
                        "fields": {"count()": 150.0, "avg(year)": 2005.3}
                      },
                      {
                        "id": "group:string:jazz",
                        "value": "jazz",
                        "relevance": 1.0,
                        "fields": {"count()": 80.0, "avg(year)": 1998.0}
                      }
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var response = await _searchOps.GroupByAsync<MusicDoc>(
            new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(output(count())))" });

        Assert.Equal(500, response.TotalCount);
        Assert.Empty(response.Hits);
        Assert.Single(response.GroupingResults);

        var genreList = response.GroupingResults[0];
        Assert.Equal("genre", genreList.Label);
        Assert.Equal(2, genreList.Groups.Count);

        var rock = genreList.Groups[0];
        Assert.Equal("rock", rock.Value);
        Assert.Equal(150.0, rock.Aggregations["count()"]);
        Assert.Equal(2005.3, rock.Aggregations["avg(year)"]);

        var jazz = genreList.Groups[1];
        Assert.Equal("jazz", jazz.Value);
        Assert.Equal(80.0, jazz.Aggregations["count()"]);
    }

    [Fact]
    public async Task GroupByAsync_ParsesMixedHitsAndGrouping()
    {
        var json = """
        {
          "root": {
            "id": "toplevel",
            "relevance": 1.0,
            "fields": {"totalCount": 100},
            "children": [
              {
                "id": "group:root:0",
                "relevance": 1.0,
                "children": [
                  {
                    "id": "grouplist:genre",
                    "children": [
                      {
                        "id": "group:string:rock",
                        "value": "rock",
                        "fields": {"count()": 50.0}
                      }
                    ]
                  }
                ]
              },
              {
                "id": "id:test:music::1",
                "relevance": 0.9,
                "source": "content",
                "fields": {"title": "Bohemian Rhapsody"}
              }
            ]
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var response = await _searchOps.GroupByAsync<MusicDoc>(
            new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(output(count())))" });

        Assert.Single(response.GroupingResults);
        Assert.Single(response.Hits);
        Assert.Equal("Bohemian Rhapsody", response.Hits[0].Fields?.Title);
    }

    [Fact]
    public async Task GroupByAsync_EmptyGroupingResult_HandledGracefully()
    {
        var json = """
        {
          "root": {
            "id": "toplevel",
            "relevance": 1.0,
            "fields": {"totalCount": 0},
            "children": []
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var response = await _searchOps.GroupByAsync<MusicDoc>(
            new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(output(count())))" });

        Assert.Empty(response.Hits);
        Assert.Empty(response.GroupingResults);
        Assert.Equal(0, response.TotalCount);
    }

    [Fact]
    public async Task GroupByAsync_HttpError_ThrowsVespaException()
    {
        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("bad request")
        });

        await Assert.ThrowsAsync<VespaException>(() =>
            _searchOps.GroupByAsync<MusicDoc>(
                new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(output(count())))" }));
    }

    [Fact]
    public async Task GroupByAsync_ParsesNestedGroups()
    {
        var json = """
        {
          "root": {
            "id": "toplevel",
            "relevance": 1.0,
            "fields": {"totalCount": 200},
            "children": [
              {
                "id": "group:root:0",
                "relevance": 1.0,
                "children": [
                  {
                    "id": "grouplist:genre",
                    "children": [
                      {
                        "id": "group:string:rock",
                        "value": "rock",
                        "fields": {"count()": 100.0},
                        "children": [
                          {
                            "id": "grouplist:year",
                            "children": [
                              {
                                "id": "group:long:2020",
                                "value": "2020",
                                "fields": {"count()": 30.0}
                              },
                              {
                                "id": "group:long:2021",
                                "value": "2021",
                                "fields": {"count()": 70.0}
                              }
                            ]
                          }
                        ]
                      }
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var response = await _searchOps.GroupByAsync<MusicDoc>(
            new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(output(count()) all(group(year) each(output(count())))))" });

        var genreList = response.GroupingResults[0];
        var rock = genreList.Groups[0];
        Assert.Equal("rock", rock.Value);
        Assert.Single(rock.SubGroups);

        var yearList = rock.SubGroups[0];
        Assert.Equal("year", yearList.Label);
        Assert.Equal(2, yearList.Groups.Count);
        Assert.Equal("2020", yearList.Groups[0].Value);
        Assert.Equal(30.0, yearList.Groups[0].Aggregations["count()"]);
    }

    [Fact]
    public async Task GroupByAsync_TotalCount_ParsedCorrectly()
    {
        var json = """
        {
          "root": {
            "id": "toplevel",
            "relevance": 1.0,
            "fields": {"totalCount": 42},
            "children": [
              {
                "id": "group:root:0",
                "children": [
                  {
                    "id": "grouplist:genre",
                    "children": []
                  }
                ]
              }
            ]
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var response = await _searchOps.GroupByAsync<MusicDoc>(
            new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(output(count())))" });

        Assert.Equal(42, response.TotalCount);
    }

    // --- GroupingAgg: Percentile ---

    [Fact]
    public void GroupingAgg_Quantiles_ReturnsBracketedListThenExpression()
        // Vespa grammar: quantiles([0.5, 0.9], expression), values 0–1
        => Assert.Equal("quantiles([0.5, 0.9], price)", GroupingAgg.Quantiles([0.5, 0.9], "price"));

    [Fact]
    public void GroupingAgg_Quantiles_UsesInvariantCulture()
        => Assert.Equal("quantiles([0.999], latency)", GroupingAgg.Quantiles([0.999], "latency"));

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void GroupingAgg_Quantiles_OutOfRangeThrows(double quantile)
        => Assert.Throws<ArgumentOutOfRangeException>(() => GroupingAgg.Quantiles([quantile], "price"));

    [Fact]
    public void GroupingAgg_Md5_EmitsThreeArguments()
        // Vespa grammar: md5(exp, number, number)
        => Assert.Equal("md5(title, 1024, 64)", GroupingAgg.Md5("title", 1024, 64));

    [Fact]
    public void GroupingAgg_Uca_QuotesLocale()
        => Assert.Equal("""uca(name, "sv")""", GroupingAgg.Uca("name", "sv"));

    [Fact]
    public void GroupingAgg_Uca_WithStrength_QuotesBoth()
        => Assert.Equal("""uca(name, "sv", "PRIMARY")""", GroupingAgg.Uca("name", "sv", "PRIMARY"));

    // --- GroupingBuilder: GroupByFixedWidth ---

    [Fact]
    public void Build_GroupByFixedWidth_ProducesFixedwidthExpr()
    {
        var expr = GroupingBuilder.All()
            .GroupByFixedWidth("price", 100)
            .Each(e => e.Output(GroupingAgg.Count()))
            .Build();

        Assert.Contains("group(fixedwidth(price, 100))", expr);
    }

    [Fact]
    public void Build_GroupByFixedWidth_MapKeyAddressing_IsAllowed()
    {
        var expr = GroupingBuilder.All()
            .GroupByFixedWidth("""attributes{"price"}""", 10)
            .Build();

        Assert.Contains("""fixedwidth(attributes{"price"}, 10)""", expr);
    }

    [Fact]
    public void Build_GroupByFixedWidth_UsesInvariantCulture()
    {
        var expr = GroupingBuilder.All()
            .GroupByFixedWidth("score", 0.5)
            .Build();

        Assert.Contains("fixedwidth(score, 0.5)", expr);
        Assert.DoesNotContain("0,5", expr);
    }

    // --- GroupingBuilder: GroupByBuckets (predefined) ---

    [Fact]
    public void Build_GroupByBuckets_ProducesPredefinedExpr()
    {
        var expr = GroupingBuilder.All()
            .GroupByBuckets("price", (0, 100), (100, 200), (200, 500))
            .Each(e => e.Output(GroupingAgg.Count()))
            .Build();

        // Buckets are comma-separated (docs.vespa.ai/en/reference/grouping-syntax.html)
        Assert.Contains("group(predefined(price, bucket[0,100), bucket[100,200), bucket[200,500)))", expr);
    }

    [Fact]
    public void Build_GroupByBuckets_EmptyThrows()
    {
        Assert.Throws<ArgumentException>(() =>
            GroupingBuilder.All().GroupByBuckets("price", Array.Empty<(double, double)>()));
    }

    // --- GroupingBuilder: GroupByBuckets (string predefined) ---

    [Fact]
    public void Build_GroupByStringBuckets_ProducesPredefinedExpr()
    {
        var expr = GroupingBuilder.All()
            .GroupByBuckets("category", ("a", "m"), ("m", "z"))
            .Each(e => e.Output(GroupingAgg.Count()))
            .Build();

        Assert.Equal("""all(group(predefined(category, bucket["a","m"), bucket["m","z"))) each(output(count())))""", expr);
    }

    [Fact]
    public void Build_GroupByStringBuckets_EscapesQuotesAndBackslashes()
    {
        var expr = GroupingBuilder.All()
            .GroupByBuckets("category", ("a\"b", "z\\"))
            .Build();

        Assert.Contains("""bucket["a\"b","z\\")""", expr);
    }

    [Fact]
    public void Build_GroupByStringBuckets_EmptyThrows()
    {
        Assert.Throws<ArgumentException>(() =>
            GroupingBuilder.All().GroupByBuckets("category", Array.Empty<(string, string)>()));
    }

    // --- Bucket group ID parsing ---

    [Fact]
    public async Task GroupByAsync_ParsesBucketGroupIds()
    {
        var json = """
        {
          "root": {
            "id": "toplevel",
            "relevance": 1.0,
            "fields": {"totalCount": 24},
            "children": [
              {
                "id": "group:root:0",
                "relevance": 1.0,
                "children": [
                  {
                    "id": "grouplist:price",
                    "label": "price",
                    "children": [
                      {
                        "id": "group:long_bucket:0:100",
                        "relevance": 1.0,
                        "fields": {"count()": 3.0}
                      },
                      {
                        "id": "group:long_bucket:100:500",
                        "relevance": 1.0,
                        "fields": {"count()": 19.0}
                      },
                      {
                        "id": "group:null",
                        "relevance": 1.0,
                        "fields": {"count()": 2.0}
                      }
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var response = await _searchOps.GroupByAsync<MusicDoc>(
            new VespaSearchRequest { Yql = "select * from music | all(group(predefined(price, bucket[0,100), bucket[100,500))) each(output(count())))" });

        var priceList = response.GroupingResults[0];
        Assert.Equal("price", priceList.Label);
        Assert.Equal(2, priceList.Groups.Count); // group:null is skipped

        Assert.Equal("0:100", priceList.Groups[0].Value);
        Assert.Equal(3.0, priceList.Groups[0].Aggregations["count()"]);

        Assert.Equal("100:500", priceList.Groups[1].Value);
        Assert.Equal(19.0, priceList.Groups[1].Aggregations["count()"]);
    }

    [Fact]
    public async Task GroupByAsync_ParsesDoubleBucketGroupIds()
    {
        var json = """
        {
          "root": {
            "id": "toplevel",
            "relevance": 1.0,
            "fields": {"totalCount": 10},
            "children": [
              {
                "id": "group:root:0",
                "relevance": 1.0,
                "children": [
                  {
                    "id": "grouplist:score",
                    "label": "score",
                    "children": [
                      {
                        "id": "group:double_bucket:0.0:0.5",
                        "relevance": 1.0,
                        "fields": {"count()": 4.0}
                      },
                      {
                        "id": "group:double_bucket:0.5:1.0",
                        "relevance": 1.0,
                        "fields": {"count()": 6.0}
                      }
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var response = await _searchOps.GroupByAsync<MusicDoc>(
            new VespaSearchRequest { Yql = "select * from music | all(group(predefined(score, bucket[0.0,0.5), bucket[0.5,1.0))) each(output(count())))" });

        var scoreList = response.GroupingResults[0];
        Assert.Equal(2, scoreList.Groups.Count);
        Assert.Equal("0.0:0.5", scoreList.Groups[0].Value);
        Assert.Equal("0.5:1.0", scoreList.Groups[1].Value);
    }

    // --- Grouping continuation / pagination ---

    [Fact]
    public async Task GroupByAsync_WithSummaryHits_ParsesHitlistChildren()
    {
        // each(output(summary())) returns documents per group as hitlist:* children
        var json = """
        {
          "root": {
            "id": "toplevel", "relevance": 1.0,
            "fields": {"totalCount": 100},
            "children": [
              {
                "id": "group:root:0",
                "children": [
                  {
                    "id": "grouplist:genre",
                    "children": [
                      {
                        "id": "group:string:rock", "value": "rock",
                        "fields": {"count()": 100.0},
                        "children": [
                          {
                            "id": "hitlist:hits",
                            "children": [
                              {"id": "id:test:music::1", "relevance": 0.9, "fields": {"title": "Song A"}},
                              {"id": "id:test:music::2", "relevance": 0.8, "fields": {"title": "Song B"}}
                            ]
                          }
                        ]
                      }
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var response = await _searchOps.GroupByAsync<MusicDoc>(
            new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(max(2) each(output(summary()))))" });

        var group = response.GroupingResults[0].Groups[0];
        Assert.Equal(2, group.Hits.Count);
        var hit = Assert.IsType<SearchHit<MusicDoc>>(group.Hits[0]);
        Assert.Equal("Song A", hit.Fields!.Title);
        Assert.Equal("1", hit.Id);
    }

    [Fact]
    public async Task GroupByAsync_WithContinuationInResponse_ReturnsContinuationToken()
    {
        var json = """
        {
          "root": {
            "id": "toplevel",
            "relevance": 1.0,
            "fields": {"totalCount": 1000},
            "children": [
              {
                "id": "group:root:0",
                "continuation": {"next": "BGAAABEBEBC"},
                "children": [
                  {
                    "id": "grouplist:genre",
                    "children": [
                      {"id": "group:string:rock", "value": "rock", "fields": {"count()": 100.0}}
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var response = await _searchOps.GroupByAsync<MusicDoc>(
            new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(output(count())))" });

        Assert.Equal("BGAAABEBEBC", response.Continuation);
    }

    [Fact]
    public async Task GroupByAsync_WithoutContinuationInResponse_ContinuationIsNull()
    {
        var json = """
        {
          "root": {
            "id": "toplevel",
            "relevance": 1.0,
            "fields": {"totalCount": 50},
            "children": [
              {
                "id": "group:root:0",
                "children": [
                  {
                    "id": "grouplist:genre",
                    "children": [
                      {"id": "group:string:rock", "value": "rock", "fields": {"count()": 50.0}}
                    ]
                  }
                ]
              }
            ]
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        });

        var response = await _searchOps.GroupByAsync<MusicDoc>(
            new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(output(count())))" });

        Assert.Null(response.Continuation);
    }

    [Fact]
    public async Task GroupByStreamAsync_DoesNotMutateCallerRequest()
    {
        var pageWithContinuation = """
        {
          "root": {
            "id": "toplevel", "relevance": 1.0,
            "fields": {"totalCount": 200},
            "children": [
              {
                "id": "group:root:0",
                "continuation": {"next": "token-page2"},
                "children": [
                  {"id": "grouplist:genre", "children": [
                    {"id": "group:string:rock", "value": "rock", "fields": {"count()": 100.0}}
                  ]}
                ]
              }
            ]
          }
        }
        """;
        var lastPage = """
        {
          "root": {
            "id": "toplevel", "relevance": 1.0,
            "fields": {"totalCount": 200},
            "children": [{"id": "group:root:0", "children": []}]
          }
        }
        """;
        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(pageWithContinuation) });
        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(lastPage) });

        var request = new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(output(count())))" };
        await foreach (var _ in _searchOps.GroupByStreamAsync<MusicDoc>(request)) { }

        // A stale continuation here would poison subsequent GroupByAsync calls with the same request
        Assert.Null(request.GroupingContinuation);
    }

    [Fact]
    public async Task GroupByStreamAsync_TwoPages_YieldsBothAndSendsCorrectTokens()
    {
        var page1 = """
        {
          "root": {
            "id": "toplevel", "relevance": 1.0,
            "fields": {"totalCount": 200},
            "children": [
              {
                "id": "group:root:0",
                "continuation": {"next": "token-page2"},
                "children": [
                  {"id": "grouplist:genre", "children": [
                    {"id": "group:string:rock", "value": "rock", "fields": {"count()": 100.0}}
                  ]}
                ]
              }
            ]
          }
        }
        """;

        var page2 = """
        {
          "root": {
            "id": "toplevel", "relevance": 1.0,
            "fields": {"totalCount": 200},
            "children": [
              {
                "id": "group:root:0",
                "children": [
                  {"id": "grouplist:genre", "children": [
                    {"id": "group:string:jazz", "value": "jazz", "fields": {"count()": 80.0}}
                  ]}
                ]
              }
            ]
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page1) });
        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page2) });

        var request = new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(output(count())))" };
        var pages = new List<GroupingSearchResponse<MusicDoc>>();
        await foreach (var page in _searchOps.GroupByStreamAsync<MusicDoc>(request))
            pages.Add(page);

        Assert.Equal(2, pages.Count);
        Assert.Equal(2, _mockHandler.Requests.Count);
        Assert.Equal("rock", pages[0].GroupingResults[0].Groups[0].Value);
        Assert.Equal("jazz", pages[1].GroupingResults[0].Groups[0].Value);
    }

    [Fact]
    public async Task GroupByStreamAsync_SinglePage_YieldsOnlyOnePage()
    {
        var json = """
        {
          "root": {
            "id": "toplevel", "relevance": 1.0,
            "fields": {"totalCount": 50},
            "children": [
              {
                "id": "group:root:0",
                "children": [
                  {"id": "grouplist:genre", "children": [
                    {"id": "group:string:rock", "value": "rock", "fields": {"count()": 50.0}}
                  ]}
                ]
              }
            ]
          }
        }
        """;

        _mockHandler.QueueResponse(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

        var request = new VespaSearchRequest { Yql = "select * from music | all(group(genre) each(output(count())))" };
        var pages = new List<GroupingSearchResponse<MusicDoc>>();
        await foreach (var page in _searchOps.GroupByStreamAsync<MusicDoc>(request))
            pages.Add(page);

        Assert.Single(pages);
        Assert.Single(_mockHandler.Requests);
    }

    // --- VespaGroupList and VespaGroup records ---

    [Fact]
    public void VespaGroupList_RecordEquality()
    {
        var g1 = new VespaGroupList("genre", [new VespaGroup("rock", new Dictionary<string, double> { ["count()"] = 10 }, [])]);
        var g2 = new VespaGroupList("genre", [new VespaGroup("rock", new Dictionary<string, double> { ["count()"] = 10 }, [])]);
        Assert.Equal(g1.Label, g2.Label);
    }

    // --- M10: Time functions ---

    [Fact] public void GroupingAgg_TimeDate() => Assert.Equal("time.date(timestamp)", GroupingAgg.TimeDate("timestamp"));
    [Fact] public void GroupingAgg_TimeYear() => Assert.Equal("time.year(timestamp)", GroupingAgg.TimeYear("timestamp"));
    [Fact] public void GroupingAgg_TimeMonthOfYear() => Assert.Equal("time.monthofyear(timestamp)", GroupingAgg.TimeMonthOfYear("timestamp"));
    [Fact] public void GroupingAgg_TimeDayOfMonth() => Assert.Equal("time.dayofmonth(timestamp)", GroupingAgg.TimeDayOfMonth("timestamp"));
    [Fact] public void GroupingAgg_TimeHourOfDay() => Assert.Equal("time.hourofday(timestamp)", GroupingAgg.TimeHourOfDay("timestamp"));
    [Fact] public void GroupingAgg_TimeMinuteOfHour() => Assert.Equal("time.minuteofhour(timestamp)", GroupingAgg.TimeMinuteOfHour("timestamp"));
    [Fact] public void GroupingAgg_TimeSecondOfMinute() => Assert.Equal("time.secondofminute(timestamp)", GroupingAgg.TimeSecondOfMinute("timestamp"));

    // --- M10: Math functions ---

    [Fact] public void GroupingAgg_MathFloor() => Assert.Equal("math.floor(price)", GroupingAgg.MathFloor("price"));
    [Fact] public void GroupingAgg_MathLog() => Assert.Equal("math.log(score)", GroupingAgg.MathLog("score"));
    [Fact] public void GroupingAgg_MathSqrt() => Assert.Equal("math.sqrt(variance)", GroupingAgg.MathSqrt("variance"));
    [Fact] public void GroupingAgg_MathAbs() => Assert.Equal("math.abs(delta)", GroupingAgg.MathAbs("delta"));
    [Fact] public void GroupingAgg_MathPow() => Assert.Equal("math.pow(score, 2)", GroupingAgg.MathPow("score", 2));

    // --- M10: Composite expressions ---

    [Fact] public void GroupingAgg_Cat() => Assert.Equal("cat(first, last)", GroupingAgg.Cat("first", "last"));
    [Fact] public void GroupingAgg_ZCurveX() => Assert.Equal("zcurve.x(position)", GroupingAgg.ZCurveX("position"));
    [Fact] public void GroupingAgg_ZCurveY() => Assert.Equal("zcurve.y(position)", GroupingAgg.ZCurveY("position"));
    [Fact] public void GroupingAgg_DocIdNsSpecific() => Assert.Equal("docidnsspecific()", GroupingAgg.DocIdNsSpecific());

    // --- M10: Aggregation math ---

    [Fact] public void GroupingAgg_Add() => Assert.Equal("add(sum(a), sum(b))", GroupingAgg.Add("sum(a)", "sum(b)"));
    [Fact] public void GroupingAgg_Mul() => Assert.Equal("mul(count(), avg(x))", GroupingAgg.Mul("count()", "avg(x)"));
    [Fact] public void GroupingAgg_Div() => Assert.Equal("div(sum(revenue), count())", GroupingAgg.Div("sum(revenue)", "count()"));
    [Fact] public void GroupingAgg_Mod() => Assert.Equal("mod(id, 10)", GroupingAgg.Mod("id", "10"));
    [Fact] public void GroupingAgg_Neg() => Assert.Equal("neg(count())", GroupingAgg.Neg("count()"));

    // --- M10: Time-based grouping YQL output ---

    [Fact]
    public void Build_GroupByTimeYear_ProducesCorrectYql()
    {
        var g = GroupingBuilder.All()
            .Group(GroupingAgg.TimeYear("timestamp"))
            .Max(20)
            .Each(e => e.Output(GroupingAgg.Count()))
            .Build();

        Assert.Equal("all(group(time.year(timestamp)) max(20) each(output(count())))", g);
    }

    [Fact]
    public void Build_GroupByMathFloor_ProducesCorrectYql()
    {
        var g = GroupingBuilder.All()
            .Group(GroupingAgg.MathFloor("price"))
            .Each(e => e.Output(GroupingAgg.Count(), GroupingAgg.Avg("price")))
            .Build();

        Assert.Contains("group(math.floor(price))", g);
    }

    // --- M10: where(true) ---

    [Fact]
    public void Build_WhereTrue_IncludesWhereClause()
    {
        var g = GroupingBuilder.All()
            .Group("genre")
            .WhereTrue()
            .Each(e => e.Output(GroupingAgg.Count()))
            .Build();

        Assert.Contains("where(true)", g);
    }

    // --- M10: alias ---

    [Fact]
    public void Build_Alias_IncludesAliasClause()
    {
        var g = GroupingBuilder.All()
            .Group("genre")
            .Alias("mycount", "count()")
            .Each(e => e.Output("$mycount"))
            .Build();

        Assert.Contains("alias(mycount, count())", g);
    }

    // --- M10: summary(class) in each ---

    [Fact]
    public void EachGroupingBuilder_Summary_EmitsNestedHitEach()
    {
        // Vespa grammar: hits per group come from each(max(N) each(output(summary(class))))
        var g = GroupingBuilder.All()
            .Group("genre")
            .Each(e => e.Output(GroupingAgg.Count()).Summary("compact", maxHits: 3))
            .Build();

        Assert.Equal("all(group(genre) each(output(count()) max(3) each(output(summary(compact)))))", g);
    }

    [Fact]
    public void EachGroupingBuilder_SummaryDefault_EmitsNestedHitEach()
    {
        var g = GroupingBuilder.All()
            .Group("genre")
            .Each(e => e.Summary())
            .Build();

        Assert.Equal("all(group(genre) each(each(output(summary()))))", g);
    }

    // --- M10: CustomParameters / param substitution ---

    [Fact]
    public void CustomParameters_SerializedAsTopLevelKeys()
    {
        var req = new VespaSearchRequest
        {
            Yql = "select * from music where userInput(@text);",
            CustomParameters = new() { ["text"] = "rock and roll" }
        };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"text\":\"rock and roll\"", json);
    }

    [Fact]
    public void CustomParameters_MultipleParams_AllSerialized()
    {
        var req = new VespaSearchRequest
        {
            Yql = "select * from music where title contains @term and year > @minYear;",
            CustomParameters = new()
            {
                ["term"] = "jazz",
                ["minYear"] = 2000
            }
        };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.Contains("\"term\":\"jazz\"", json);
        Assert.Contains("\"minYear\":2000", json);
    }

    [Fact]
    public void CustomParameters_NullWhenNotSet_NotEmitted()
    {
        var req = new VespaSearchRequest { Yql = "select * from music;" };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        Assert.DoesNotContain("customParameters", json);
    }

    // --- M13: cmp() aggregation comparison ---

    [Fact] public void GroupingAgg_Cmp() => Assert.Equal("cmp(price, cost)", GroupingAgg.Cmp("price", "cost"));

    // --- M13: fixedwidth() expression helper ---

    [Fact] public void GroupingAgg_FixedWidth() => Assert.Equal("fixedwidth(price, 100)", GroupingAgg.FixedWidth("price", 100));

    [Fact]
    public void GroupingAgg_FixedWidth_FloatWidth_UsesInvariantCulture()
        => Assert.Equal("fixedwidth(score, 0.25)", GroupingAgg.FixedWidth("score", 0.25));

    private class MusicDoc
    {
        public string Title { get; set; } = string.Empty;
    }
}
