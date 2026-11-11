using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.Extensions.VectorData;
using Xunit;

namespace Vespa.VectorData.Tests;

public sealed class FilterFeatureTests
{
    [Fact]
    public async Task CapturedStringMembership_PreservesCaseAndEscapesEveryTerm()
    {
        var allowed = new[] { "Engineering", "a\") or true or category contains (\"", "Engineering" };

        var yql = await Translate(r => allowed.Contains(r.Category));

        Assert.Contains($"category contains ({{stem:false,normalizeCase:false,accentDrop:false}}{JsonSerializer.Serialize(allowed[0])})", yql);
        Assert.Contains($"category contains ({{stem:false,normalizeCase:false,accentDrop:false}}{JsonSerializer.Serialize(allowed[1])})", yql);
        Assert.Contains(" or ", yql);
        Assert.DoesNotContain("category in", yql);
        Assert.Equal(1, yql.Split(JsonSerializer.Serialize("Engineering"), StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task CapturedListAndInlineIntegerSets_UseIntegerIn()
    {
        List<int> allowed = [2, 4, 2];
        Assert.Contains("number in (2, 4)", await Translate(r => allowed.Contains(r.Number)));
        Assert.Contains("number in (1, 3)", await Translate(r => new[] { 1, 3 }.Contains(r.Number)));
        Assert.Contains("number in (1, 3)", await Translate(r => Enumerable.Contains(new[] { 1, 3 }, r.Number)));
    }

    [Fact]
    public async Task ArrayAndListMembership_UseElementMatching()
    {
        var tag = "C#";
        Assert.Contains("tags contains ({stem:false,normalizeCase:false,accentDrop:false}\"C#\")", await Translate(r => r.Tags.Contains(tag)));
        Assert.Contains("numbers = 2", await Translate(r => r.Numbers.Contains(2)));
        Assert.Contains("!(numbers = 2)", await Translate(r => !r.Numbers.Contains(2)));
    }

    [Fact]
    public async Task LimitedAny_MatchesAnElementAgainstTheAllowedSet()
    {
        string[] tags = ["Alpha", "Beta"];
        long[] numbers = [1, 2];
        var strings = await Translate(r => r.Tags.Any(value => tags.Contains(value)));
        Assert.Contains("tags contains ({stem:false,normalizeCase:false,accentDrop:false}\"Alpha\")", strings);
        Assert.Contains(" or ", strings);
        Assert.Contains("numbers in (1L, 2L)", await Translate(r => r.Numbers.Any(value => numbers.Contains(value))));
    }

    [Fact]
    public async Task EmptyMembershipSets_TranslateToFalseWithoutInventingAnEmptyTerm()
    {
        int[] empty = [];
        Assert.EndsWith("where false", await Translate(r => empty.Contains(r.Number)));
        Assert.EndsWith("where !(false)", await Translate(r => !empty.Contains(r.Number)));
        Assert.EndsWith("where false", await Translate(r => r.Numbers.Any(value => empty.Contains(value))));
    }

    [Fact]
    public async Task IntegerPromotions_PreserveLongValuesAndDoNotNarrowTheProperty()
    {
        long[] allowed = [1, (long)int.MaxValue + 1];
        Assert.Contains("number in (1L, 2147483648L)", await Translate(r => allowed.Contains(r.Number)));
        await AssertRejected(r => new[] { 1, 2 }.Contains((int)r.LongNumber));
        await AssertRejected(r => new[] { 1f, 2f }.Contains((float)r.Number));
    }

    [Fact]
    public async Task Dates_UseTheSameUtcTickAndDayCodecsAsStoredFields()
    {
        var utc = new DateTime(2026, 11, 10, 12, 30, 0, DateTimeKind.Utc).AddTicks(1);
        var offset = new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(3));
        var date = new DateOnly(2026, 11, 10);
        var ticks = (utc.Ticks - DateTime.UnixEpoch.Ticks).ToString(CultureInfo.InvariantCulture) + "L";
        var days = (date.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber).ToString(CultureInfo.InvariantCulture);

        Assert.Contains("created >= " + ticks, await Translate(r => r.Created >= utc));
        Assert.Contains("updated < " + ticks, await Translate(r => offset > r.Updated));
        Assert.Contains("day = " + days, await Translate(r => r.Day == date));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task NonUtcDateTime_IsRejectedBeforeQuerying(DateTimeKind kind)
    {
        using var harness = new TestHarness();
        var cutoff = new DateTime(2026, 11, 10, 12, 0, 0, kind);
        var collection = harness.Store.GetCollection<string, FilterRecord>("article");
        await Assert.ThrowsAnyAsync<ArgumentException>(() => collection.GetAsync(r => r.Created > cutoff, 1).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    [Fact]
    public async Task NullEmptyAndFullTextEquality_AreRejectedBeforeQuerying()
    {
        string?[] withNull = ["a", null];
        string[] withEmpty = ["a", ""];
        await AssertRejected(r => withNull.Contains(r.Category));
        await AssertRejected(r => withEmpty.Contains(r.Category));
        await AssertRejected(r => r.Tags.Contains(""));
        await AssertRejected(r => r.Tags.Contains(null!));
        await AssertRejected(r => r.OptionalCreated == null);
        await AssertRejected(r => r.FullText == "several words");
    }

    [Fact]
    public async Task UnsupportedPredicatesComparersAndCollectionEquality_AreNotApproximated()
    {
        string[] allowed = ["A", "B"];
        await AssertRejected(r => r.Tags.Any(value => value == "A" || value == "B"));
        await AssertRejected(r => allowed.Contains(r.Category, StringComparer.OrdinalIgnoreCase));
        await AssertRejected(r => r.Category.Contains("A"));
        await AssertRejected(r => r.Tags == allowed);
        await AssertRejected(r => r.Numbers.Count > 1);
    }

    [Fact]
    public async Task CapturedCustomEnumerableAndPropertyGetter_AreNeverExecuted()
    {
        var executed = false;
        IEnumerable<int> Values()
        {
            executed = true;
            yield return 1;
        }
        var values = Values();
        await AssertRejected(r => values.Contains(r.Number));
        Assert.False(executed);
        var owner = new ComputedValues();
        await AssertRejected(r => owner.Values.Contains(r.Number));
        Assert.Equal(0, owner.Reads);
    }

    [Fact]
    public async Task DynamicArrayMembership_HonorsMappedTypesAndStorageNames()
    {
        using var harness = EmptyHarness();
        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            [
                new VectorStoreKeyProperty("Key", typeof(string)),
                new VectorStoreDataProperty("Tags", typeof(string[])) { StorageName = "tags", IsIndexed = true },
                new VectorStoreDataProperty("Number", typeof(int)) { StorageName = "number", IsIndexed = true }
            ]
        };
        var collection = harness.Store.GetDynamicCollection("article", definition);
        var allowed = new[] { "C#", "F#" };
        await collection.GetAsync(r => ((string[])r["Tags"]!).Any(value => allowed.Contains(value)), 1).Collect();
        Assert.Contains("tags contains", Query(harness));
        await Assert.ThrowsAsync<NotSupportedException>(() => collection.GetAsync(r => new[] { 1L, 2L }.Contains((long)r["Number"]!), 1).Collect());
        Assert.Single(harness.Handler.Requests);
    }

    private static async Task<string> Translate(Expression<Func<FilterRecord, bool>> filter)
    {
        using var harness = EmptyHarness();
        await harness.Store.GetCollection<string, FilterRecord>("article").GetAsync(filter, 1).Collect();
        return Query(harness);
    }

    private static async Task AssertRejected(Expression<Func<FilterRecord, bool>> filter)
    {
        using var harness = new TestHarness();
        await Assert.ThrowsAsync<NotSupportedException>(() => harness.Store.GetCollection<string, FilterRecord>("article")
            .GetAsync(filter, 1).Collect());
        Assert.Empty(harness.Handler.Requests);
    }

    private static TestHarness EmptyHarness() => new((_, _) => TestHarness.Json("""{"root":{"children":[]}}"""));

    private static string Query(TestHarness harness)
    {
        using var body = JsonDocument.Parse(Assert.Single(harness.Handler.Requests).Body!);
        return body.RootElement.GetProperty("yql").GetString()!;
    }

    public sealed class FilterRecord
    {
        [VectorStoreKey] public string Id { get; set; } = "";
        [VectorStoreData(StorageName = "category", IsIndexed = true)] public string Category { get; set; } = "";
        [VectorStoreData(StorageName = "number", IsIndexed = true)] public int Number { get; set; }
        [VectorStoreData(StorageName = "long_number", IsIndexed = true)] public long LongNumber { get; set; }
        [VectorStoreData(StorageName = "tags", IsIndexed = true)] public string[] Tags { get; set; } = [];
        [VectorStoreData(StorageName = "numbers", IsIndexed = true)] public List<int> Numbers { get; set; } = [];
        [VectorStoreData(StorageName = "created", IsIndexed = true)] public DateTime Created { get; set; }
        [VectorStoreData(StorageName = "optional_created", IsIndexed = true)] public DateTime? OptionalCreated { get; set; }
        [VectorStoreData(StorageName = "updated", IsIndexed = true)] public DateTimeOffset Updated { get; set; }
        [VectorStoreData(StorageName = "day", IsIndexed = true)] public DateOnly Day { get; set; }
        [VectorStoreData(StorageName = "fulltext", IsIndexed = true, IsFullTextIndexed = true)] public string FullText { get; set; } = "";
    }

    private sealed class ComputedValues
    {
        public int Reads { get; private set; }
        public int[] Values
        {
            get
            {
                Reads++;
                return [1, 2];
            }
        }
    }
}
