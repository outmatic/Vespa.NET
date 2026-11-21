using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Vespa.AgentFrameworkSample;
using Xunit;

namespace Vespa.AgentFramework.Tests;

public sealed class NativeRetrievalTests
{
    [Theory]
    [InlineData("lexical")]
    [InlineData("semantic")]
    [InlineData("hybrid_normalized")]
    [InlineData("hybrid_rrf")]
    public void NativeRequest_BindsUntrustedTextAndKeepsFiltersOutsideCandidateUnion(string mode)
    {
        const string query = "title:\"hello\" or true) | all(group(user_id))";
        var request = NativeRetrieval.CreateRequest(query, [1, 0], "article", "published = true",
            new() { Mode = mode, VectorCandidates = 40, KeywordCandidates = 80, VectorWeight = .3, TitleWeight = 2, RrfK = 30 });
        Assert.DoesNotContain(query, request.Yql, StringComparison.Ordinal);
        Assert.Contains("where (published = true) and (", request.Yql, StringComparison.Ordinal);
        Assert.Equal(mode != "lexical", request.Yql.Contains("nearestNeighbor", StringComparison.Ordinal));
        Assert.Equal(mode != "semantic", request.Yql.Contains("userInput(@lexical_query)", StringComparison.Ordinal));
        if (mode != "semantic")
            Assert.Equal(query, request.CustomParameters!["lexical_query"]);
        Assert.Equal(mode != "lexical", request.Input!.ContainsKey("query(query_embedding)"));
        if (mode.StartsWith("hybrid_", StringComparison.Ordinal))
        {
            Assert.Contains(" or ", request.Yql, StringComparison.Ordinal);
            Assert.Equal(120, request.Ranking!.GlobalPhaseRerankCount);
            Assert.Equal(.3, request.Input["query(vector_weight)"]);
        }
    }

    [Theory]
    [InlineData(-.1, 1)]
    [InlineData(1.1, 1)]
    [InlineData(double.NaN, 1)]
    [InlineData(.5, 0)]
    public void InvalidTuningParameters_AreRejected(double weight, double titleWeight) =>
        Assert.Throws<ArgumentException>(() => NativeRetrieval.CreateRequest("query", [1, 0], "article", "true",
            new() { Mode = "hybrid_normalized", VectorWeight = weight, TitleWeight = titleWeight }));

    [Fact]
    public void NativeOptions_ReachTheRequestWithoutClientSideTextProcessing()
    {
        var request = NativeRetrieval.CreateRequest("Caffè ZX-881", [1, 0], "article", "published = true", new()
        {
            Mode = "hybrid_normalized",
            Language = "it",
            TextFieldSet = "body",
            Grammar = "phrase",
            ResultFields = ["body", "body"],
            SummaryClass = "vector_data",
            RankProfileOverride = "custom_profile",
            HnswExploreAdditionalHits = 64
        });
        Assert.Equal("it", request.ModelLanguage);
        Assert.Equal("vector_data", request.PresentationSummary);
        Assert.Equal("custom_profile", request.Ranking!.Profile);
        Assert.StartsWith("select documentid, body from", request.Yql, StringComparison.Ordinal);
        Assert.Contains("grammar:\"phrase\",defaultIndex:\"body\"", request.Yql, StringComparison.Ordinal);
        Assert.Contains("approximate:true,hnsw.exploreAdditionalHits:64", request.Yql, StringComparison.Ordinal);
        Assert.Equal("Caffè ZX-881", request.CustomParameters!["lexical_query"]);
    }

    [Fact]
    public void ExactSearch_OverridesApproximationAndOmitsHnswExploration()
    {
        var request = NativeRetrieval.CreateRequest("query", [1, 0], "article", "true",
            new() { HnswExploreAdditionalHits = 64 }, approximate: false);
        Assert.Contains("approximate:false", request.Yql, StringComparison.Ordinal);
        Assert.DoesNotContain("hnsw.exploreAdditionalHits", request.Yql, StringComparison.Ordinal);
    }

    [Fact]
    public void GlobalRerankingBudget_IsIndependentOfCandidateTargets()
    {
        var request = NativeRetrieval.CreateRequest("query", [1, 0], "article", "true", new()
        {
            Mode = "hybrid_normalized",
            VectorCandidates = 600,
            KeywordCandidates = 300,
            GlobalPhaseRerankCount = 100
        });
        Assert.Equal(100, request.Ranking!.GlobalPhaseRerankCount);
        Assert.Contains("totalTargetHits:600", request.Yql, StringComparison.Ordinal);
        Assert.Contains("totalTargetHits:300", request.Yql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "hybrid_normalized")]
    [InlineData(2, "hybrid_normalized")]
    [InlineData(2001, "hybrid_normalized")]
    [InlineData(100, "semantic")]
    public void InvalidGlobalRerankingBudget_IsRejected(int rerank, string mode) =>
        Assert.Throws<ArgumentException>(() => NativeRetrieval.CreateRequest("query", [1, 0], "article", "true",
            new() { Mode = mode, GlobalPhaseRerankCount = rerank }));

    [Fact]
    public void WeakAndTuning_UsesNativeQueryParametersAndPreservesRawInput()
    {
        var request = NativeRetrieval.CreateRequest("the \"whole\" paragraph", [1, 0], "article", "true", new()
        {
            Mode = "hybrid_normalized",
            Grammar = "tokenize",
            WeakAndStopwordLimit = .6,
            WeakAndAdjustTarget = .01
        });
        Assert.Equal(.6, request.CustomParameters!["ranking.matching.weakand.stopwordLimit"]);
        Assert.Equal(.01, request.CustomParameters["ranking.matching.weakand.adjustTarget"]);
        Assert.Equal("the \"whole\" paragraph", request.CustomParameters["lexical_query"]);
    }

    [Theory]
    [InlineData(-.1, "lexical", "tokenize")]
    [InlineData(double.NaN, "lexical", "tokenize")]
    [InlineData(1.1, "hybrid_normalized", "weakAnd")]
    [InlineData(.6, "semantic", "weakAnd")]
    [InlineData(.6, "lexical", "phrase")]
    public void InvalidWeakAndTuning_IsRejected(double limit, string mode, string grammar) =>
        Assert.Throws<ArgumentException>(() => NativeRetrieval.CreateRequest("query", [1, 0], "article", "true",
            new() { Mode = mode, Grammar = grammar, WeakAndStopwordLimit = limit }));

    [Theory]
    [InlineData("body) or true", "body", "weakAnd")]
    [InlineData("default", "*", "weakAnd")]
    [InlineData("default", "body", "arbitrary")]
    public void InvalidNativeOptions_AreRejectedBeforeSending(string fieldset, string resultField, string grammar) =>
        Assert.Throws<ArgumentException>(() => NativeRetrieval.CreateRequest("query", [1, 0], "article", "true",
            new() { TextFieldSet = fieldset, ResultFields = [resultField], Grammar = grammar }));

    [Fact]
    public async Task LanguageProjectionAndSummary_AreSerializedToVespa()
    {
        using var handler = new RetrievalHandler();
        using var http = new HttpClient(handler);
        using var client = new VespaClient(http, new() { Endpoint = "http://localhost:8080", EnableRetry = false });
        using var embeddings = new CountingEmbeddings();
        await new DocumentationSearch(client, embeddings, new()
        {
            Mode = "lexical",
            Language = "it",
            ResultFields = ["body"],
            SummaryClass = "vector_data"
        }).SearchAsync("caffè", default);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal("it", body.RootElement.GetProperty("model.language").GetString());
        Assert.Equal("vector_data", body.RootElement.GetProperty("presentation.summary").GetString());
        Assert.StartsWith("select documentid, body from", body.RootElement.GetProperty("yql").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LexicalSearch_UsesNativeFieldsetWithoutGeneratingAnEmbedding()
    {
        using var handler = new RetrievalHandler();
        using var http = new HttpClient(handler);
        using var client = new VespaClient(http, new() { Endpoint = "http://localhost:8080", EnableRetry = false });
        using var embeddings = new CountingEmbeddings();
        var search = new DocumentationSearch(client, embeddings, new() { Mode = "lexical" });
        var hit = Assert.Single(await search.SearchAsync("ZX-881", default));
        Assert.Equal("source", hit.Id);
        Assert.Equal(0, embeddings.Calls);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Equal("ZX-881", body.RootElement.GetProperty("lexical_query").GetString());
        Assert.Equal("lexical", body.RootElement.GetProperty("ranking").GetProperty("profile").GetString());
    }

    [Fact]
    public async Task MicrosoftTextSearchProvider_UsesSemanticSampleDefaultsWithoutReplacingItsPipeline()
    {
        using var handler = new RetrievalHandler();
        using var http = new HttpClient(handler);
        using var client = new VespaClient(http, new() { Endpoint = "http://localhost:8080", EnableRetry = false });
        using var embeddings = new CountingEmbeddings();
        var search = new DocumentationSearch(client, embeddings, RetrievalSettings.ForSample());
        using var model = new PatternChatClient();
        var provider = new TextSearchProvider(async (query, ct) =>
            (await search.SearchAsync(query, ct)).Select(hit => new TextSearchProvider.TextSearchResult
            {
                Text = hit.Fields!.Title + "\n" + hit.Fields.Body,
                SourceName = hit.Id
            }));
        var agent = model.AsAIAgent(new ChatClientAgentOptions { Name = "native-search", AIContextProviders = [provider] });
        await agent.RunAsync("How do I recover ZX-881?");
        Assert.Contains(model.LastMessages, message => message.Text.Contains("Recovery instructions", StringComparison.Ordinal));
        Assert.Equal(1, embeddings.Calls);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Contains("nearestNeighbor", body.RootElement.GetProperty("yql").GetString());
        Assert.DoesNotContain("userInput", body.RootElement.GetProperty("yql").GetString());
        Assert.Contains("approximate:true", body.RootElement.GetProperty("yql").GetString());
        Assert.Contains("totalTargetHits:100", body.RootElement.GetProperty("yql").GetString());
        Assert.Equal("vector_search", body.RootElement.GetProperty("ranking").GetProperty("profile").GetString());
        Assert.False(body.RootElement.TryGetProperty("lexical_query", out _));
        Assert.False(body.RootElement.GetProperty("ranking").TryGetProperty("globalPhase.rerankCount", out _));
        Assert.False(body.RootElement.TryGetProperty("ranking.matching.weakand.stopwordLimit", out _));
        Assert.False(body.RootElement.TryGetProperty("ranking.matching.weakand.adjustTarget", out _));
    }

    [Fact]
    public async Task HybridSampleOverride_CombinesVectorAndLexicalSearchWithExplicitBudgets()
    {
        using var handler = new RetrievalHandler();
        using var http = new HttpClient(handler);
        using var client = new VespaClient(http, new() { Endpoint = "http://localhost:8080", EnableRetry = false });
        using var embeddings = new CountingEmbeddings();
        await new DocumentationSearch(client, embeddings, RetrievalSettings.ForSample("hybrid"))
            .SearchAsync("Find a similar recovery guide", default);
        Assert.Equal(1, embeddings.Calls);
        using var body = JsonDocument.Parse(Assert.Single(handler.Requests));
        Assert.Contains("nearestNeighbor", body.RootElement.GetProperty("yql").GetString());
        Assert.Contains("userInput(@lexical_query)", body.RootElement.GetProperty("yql").GetString());
        Assert.Contains("grammar:\"tokenize\"", body.RootElement.GetProperty("yql").GetString());
        Assert.Contains("approximate:true", body.RootElement.GetProperty("yql").GetString());
        Assert.Contains("totalTargetHits:100", body.RootElement.GetProperty("yql").GetString());
        Assert.Equal("Find a similar recovery guide", body.RootElement.GetProperty("lexical_query").GetString());
        Assert.Equal("hybrid_normalized", body.RootElement.GetProperty("ranking").GetProperty("profile").GetString());
        Assert.Equal(100, body.RootElement.GetProperty("ranking").GetProperty("globalPhase.rerankCount").GetInt32());
        Assert.Equal(.5, body.RootElement.GetProperty("input").GetProperty("query(vector_weight)").GetDouble());
        Assert.False(body.RootElement.TryGetProperty("ranking.matching.weakand.stopwordLimit", out _));
        Assert.False(body.RootElement.TryGetProperty("ranking.matching.weakand.adjustTarget", out _));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task FailedOrIncompleteRetrieval_IsNotPresentedAsSuccessfulContext(bool full, bool error)
    {
        using var handler = new RetrievalHandler(full, error);
        using var http = new HttpClient(handler);
        using var client = new VespaClient(http, new() { Endpoint = "http://localhost:8080", EnableRetry = false });
        using var embeddings = new CountingEmbeddings();
        var search = new DocumentationSearch(client, embeddings, new() { Mode = "lexical" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => search.SearchAsync("ZX-881", default));
    }

    private sealed class RetrievalHandler(bool full = true, bool error = false) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    root = new
                    {
                        coverage = new { full },
                        errors = error ? new[] { new { message = "search failed" } } : null,
                        children = new[] { new { id = "id:knowledge:article::source", relevance = .9,
                            fields = new { title = "ZX-881", body = "Recovery instructions" } } }
                    }
                }), Encoding.UTF8, "application/json")
            };
        }
    }

    internal sealed class CountingEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        internal int Calls { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ =>
            {
                var vector = new float[768];
                vector[0] = 1;
                return new Embedding<float>(vector);
            })));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
