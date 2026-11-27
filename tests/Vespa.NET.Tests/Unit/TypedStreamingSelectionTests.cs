using System.Linq.Expressions;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vespa.Documents;
using Vespa.Models;
using Vespa.Models.Attributes;
using Vespa.Query;
using Vespa.Search;
using Vespa.Tests.Helpers;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class TypedStreamingSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HelpersReturnARequestWithOnlyTheSelectionReplaced(bool groups)
    {
        var request = new VespaSearchRequest
        {
            Yql = "select * from sources * where userQuery()",
            Hits = 4,
            Offset = 3,
            Ranking = new() { Profile = "semantic" },
            Input = new Dictionary<string, object?> { ["query(vector)"] = new[] { 0.25, 0.75 } },
            ModelQueryString = "keep this text",
            StreamingMaxBucketsPerVisit = 2,
            StreamingSelection = "old raw selector"
        }.WithParameter("myParameter", 21);
        var before = JsonSerializer.SerializeToNode(request, VespaJsonOptions.Default)!.AsObject();
        var originalInput = request.Input;
        var originalRanking = request.Ranking;
        var minimumPrice = 8;
        var names = new[] { "one", "two" };

        var returned = groups ? request.WithStreamingGroups(names) : request.WithStreamingSelection<Inventory>(p => p.Price > minimumPrice);
        minimumPrice = 99;
        names[1] = "changed";

        var expected = groups ? "id.group == \"one\" or id.group == \"two\"" : "(inventory and ((inventory.unit_price > 8)))";
        Assert.Equal("old raw selector", request.StreamingSelection);
        Assert.Equal(expected, returned.StreamingSelection);
        Assert.Same(originalInput, returned.Input);
        Assert.Same(originalRanking, returned.Ranking);
        before["streaming.selection"] = expected;
        Assert.True(JsonNode.DeepEquals(before, JsonSerializer.SerializeToNode(returned, VespaJsonOptions.Default)));
    }

    [Fact]
    public void GroupsUseExactEqualityAndEscapeEachNameAsOneDocumentSelectorLiteral()
    {
        var request = new VespaSearchRequest().WithStreamingGroups("quo\"te\\dir", "café📦", "team*?", "a\tb\nc\rd");
        const string expected = """
            id.group == "quo\"te\\dir" or id.group == "café📦" or id.group == "team*?" or id.group == "a\tb\nc\rd"
            """;
        Assert.Equal(expected, request.StreamingSelection);
        Assert.Null(request.StreamingGroupName);
        Assert.Equal("id.group == \"one\"", request.WithStreamingGroups("one").StreamingSelection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    [InlineData(0xFFFE)]
    [InlineData(0xFFFF)]
    public void GroupWithInvalidTextLeavesThePreviousSelectionIntact(int codeUnit)
    {
        var request = new VespaSearchRequest { StreamingSelection = "original" };
        var invalidGroup = "before" + (char)codeUnit + "after";
        Assert.ThrowsAny<ArgumentException>(() => request.WithStreamingGroups("first", invalidGroup));
        Assert.Equal("original", request.StreamingSelection);
    }

    [Fact]
    public void GroupsPreserveDelAndC1CharactersAllowedInVespaIds()
    {
        const string group = "a\u007F\u0080\u009Fb";
        var request = new VespaSearchRequest().WithStreamingGroups(group);
        Assert.Equal($"id.group == \"{group}\"", request.StreamingSelection);
    }

    [Fact]
    public void DuplicateGroupsUseOrdinalEqualityAndATerminalBackslashClauseGoesLast()
    {
        var request = new VespaSearchRequest().WithStreamingGroups("last\\", "A", "last\\", "a", "A");
        const string expected = """
            id.group == "A" or id.group == "a" or id.group == "last\\"
            """;
        Assert.Equal(expected, request.StreamingSelection);
    }

    [Fact]
    public void DistinctGroupsEndingInBackslashesFailBeforeReplacingTheSelection()
    {
        var request = new VespaSearchRequest { StreamingSelection = "original" };
        Assert.Throws<NotSupportedException>(() => request.WithStreamingGroups("left\\", "middle", "right\\\\"));
        Assert.Equal("original", request.StreamingSelection);
    }

    [Fact]
    public void TypedConstantStillScopesSelectionToTheFilterModel()
    {
        var request = new VespaSearchRequest { Yql = "select * from sources * where true" };
        Assert.Equal("(inventory and (true))", request.WithStreamingSelection<Inventory>(p => true).StreamingSelection);
        Assert.Equal("select * from sources * where true", request.Yql);
    }

    [Fact]
    public void TypedStreamingFieldEndingInBackslashAllowsAnotherQuotedComparison()
    {
        Expression<Func<Inventory, bool>> selection = p => p.Label == "tail\\" || p.Label == "look\" or true or \"x";
        const string expectedStreaming = """
            (inventory and (((inventory.label == "tail\x5C") or (inventory.label == "look\" or true or \"x"))))
            """;
        const string expectedCondition = """
            ((inventory.label == "tail\\") or (inventory.label == "look\" or true or \"x"))
            """;

        Assert.Equal(expectedStreaming, new VespaSearchRequest().WithStreamingSelection(selection).StreamingSelection);
        Assert.Equal(expectedStreaming, DocumentSelection.For(selection));
        Assert.Equal(expectedCondition, DocumentCondition.For(selection));
    }

    [Fact]
    public void InvalidArgumentsLeaveTheExistingRawSelectionIntact()
    {
        VespaSearchRequest missing = null!;
        Assert.Throws<ArgumentNullException>(() => missing.WithStreamingGroups("one"));
        Assert.Throws<ArgumentNullException>(() => missing.WithStreamingSelection<Inventory>(p => true));
        var request = new VespaSearchRequest { StreamingSelection = "original" };
        Assert.Throws<ArgumentNullException>(() => request.WithStreamingSelection<Inventory>(null!));
        Assert.Throws<ArgumentNullException>(() => request.WithStreamingGroups(null!));
        Assert.ThrowsAny<ArgumentException>(() => request.WithStreamingGroups());
        Assert.ThrowsAny<ArgumentException>(() => request.WithStreamingGroups("valid", null!));
        Assert.ThrowsAny<ArgumentException>(() => request.WithStreamingGroups("valid", " \t"));
        Assert.Throws<NotSupportedException>(() => request.WithStreamingSelection<Inventory>(p => Math.Abs(p.Price) > 0));
        Assert.Equal("original", request.StreamingSelection);
    }

    [Theory]
    [InlineData("")]
    [InlineData("other")]
    public void GroupNameConflictsLeaveBothPropertiesIntact(string group)
    {
        var request = new VespaSearchRequest { StreamingGroupName = group, StreamingSelection = "original" };
        Assert.Throws<InvalidOperationException>(() => request.WithStreamingGroups("one"));
        Assert.Throws<InvalidOperationException>(() => request.WithStreamingSelection<Inventory>(p => true));
        Assert.Equal(group, request.StreamingGroupName);
        Assert.Equal("original", request.StreamingSelection);
    }

    [Theory]
    [InlineData("streaming.groupname")]
    [InlineData("streaming.selection")]
    [InlineData("streaming.userid")]
    public void ReservedCustomParametersConflictBeforeTheSelectionIsReplaced(string key)
    {
        var request = new VespaSearchRequest { StreamingSelection = "original" }.WithParameter(key, "existing");
        Assert.Throws<InvalidOperationException>(() => request.WithStreamingGroups("one"));
        Assert.Throws<InvalidOperationException>(() => request.WithStreamingSelection<Inventory>(p => true));
        Assert.Equal("original", request.StreamingSelection);
        Assert.Equal("existing", Assert.Single(request.CustomParameters!).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PagedAndHitStreamsSerializeTheFrozenSelectionOnEveryRequest(bool hitStream)
    {
        var handler = new MockHttpMessageHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var search = new SearchOperations(http, new());
        var minimumPrice = 8;
        var request = new VespaSearchRequest { Yql = "select * from inventory where true", Hits = 9, Offset = 2 };
        request = request.WithStreamingSelection<Inventory>(p => p.Price > minimumPrice);
        const string first = """{"root":{"id":"toplevel","fields":{"totalCount":1},"children":[{"id":"id:n:inventory::a","fields":{"title":"first"}}]}}""";
        const string last = """{"root":{"id":"toplevel","fields":{"totalCount":1},"children":[]}}""";
        handler.QueueResponses(new(HttpStatusCode.OK) { Content = new StringContent(first) }, new(HttpStatusCode.OK) { Content = new StringContent(last) });

        if (hitStream)
        {
            await foreach (var hit in search.SearchStreamAsync<Projection>(request, pageSize: 1))
            {
                Assert.Equal("first", hit.Fields!.Title);
                minimumPrice = 99;
            }
        }
        else
        {
            await foreach (var page in search.SearchPagedAsync<Projection>(request, pageSize: 1))
                if (page.Root.Children.Count > 0)
                {
                    Assert.Equal("first", Assert.Single(page.Root.Children).Fields!.Title);
                    minimumPrice = 99;
                }
        }

        Assert.Equal(99, minimumPrice);
        var requests = handler.Requests;
        Assert.Equal(2, requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            using var body = JsonDocument.Parse(await requests[index].Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal("(inventory and ((inventory.unit_price > 8)))", root.GetProperty("streaming.selection").GetString());
            Assert.False(root.TryGetProperty("streaming.groupname", out _));
            Assert.Equal(2 + index, root.GetProperty("offset").GetInt32());
            Assert.Equal(1, root.GetProperty("hits").GetInt32());
            Assert.Equal(request.Yql, root.GetProperty("yql").GetString());
        }
        Assert.Equal(2, request.Offset);
        Assert.Equal(9, request.Hits);
    }

    [VespaDocument("inventory", Namespace = "tenant")]
    private sealed class Inventory
    {
        [VespaField(Name = "unit_price")]
        public int Price { get; init; }

        [VespaField(Name = "label")]
        public string? Label { get; init; }
    }

    private sealed class Projection
    {
        public string? Title { get; init; }
    }
}
