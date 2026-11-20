using System.Text.Json;
using Vespa.RetrievalEvaluation;
using Xunit;

namespace Vespa.AgentFramework.Tests;

public sealed class EvaluationCorpusTests
{
    [Fact]
    public void DeclaredEmptySourceDocument_RemainsAvailableWithItsPositiveJudgment()
    {
        var corpus = Create();
        Assert.Equal(2, corpus.Validate());
        Assert.Contains(corpus.Documents, document => document.Id == "empty");
        Assert.Equal(1, corpus.Queries[0].Relevance["empty"]);
    }

    [Fact]
    public void UndeclaredEmptySourceDocument_IsRejected()
    {
        var corpus = Create() with { Metadata = JsonSerializer.SerializeToElement(new { }) };
        Assert.Throws<ArgumentException>(() => corpus.Validate());
    }

    [Fact]
    public void DeclaringAnEmptyDocument_DoesNotPermitAnEmptyQuery()
    {
        var corpus = Create();
        corpus.Queries[0] = corpus.Queries[0] with { Text = " " };
        Assert.Throws<ArgumentException>(() => corpus.Validate());
    }

    [Fact]
    public void DeclaredEmptySourceIds_MustMatchTheActualEmptyDocuments()
    {
        var corpus = Create() with
        {
            Metadata = JsonSerializer.SerializeToElement(new { emptyDocumentIds = new[] { "empty", "unknown" } })
        };
        Assert.Throws<ArgumentException>(() => corpus.Validate());
    }

    private static EvaluationCorpus Create() => new(
        [new("empty", "", [1, 0]), new("answer", "Supporting text", [0, 1])],
        [new("question", "Find supporting text", ["supporting"], [1, 0], new() { ["empty"] = 1 })],
        JsonSerializer.SerializeToElement(new { emptyDocumentIds = new[] { "empty" } }));
}
