using System.IO.Compression;

namespace Vespa.RetrievalEvaluation;

internal static class EvaluationApplication
{
    internal static string NativeProfiles => """
        fieldset default { fields: title, body }
        rank-profile lexical {
            inputs { query(title_weight) double: 1 }
            function lexical_score() { expression: query(title_weight) * bm25(title) + bm25(body) }
            first-phase { expression: lexical_score }
            match-features: lexical_score
        }
        rank-profile vector_search {
            inputs { query(query_embedding) tensor<float>(x[DIMENSIONS]) }
            first-phase { expression: closeness(field, embedding) }
            match-features: distance(field, embedding)
        }
        rank-profile hybrid_normalized inherits vector_search {
            inputs {
                query(vector_weight) double: 0.5
                query(title_weight) double: 1
            }
            function lexical_score() { expression: query(title_weight) * bm25(title) + bm25(body) }
            function semantic_score() { expression: cos(distance(field, embedding)) }
            first-phase { expression: log(1 + lexical_score) + closeness(field, embedding) }
            global-phase {
                expression: (1-query(vector_weight)) * normalize_linear(lexical_score) + query(vector_weight) * normalize_linear(semantic_score)
                rerank-count: 200
            }
            match-features: lexical_score semantic_score
        }
        """ + "\n" + string.Join("\n", new[] { 10, 30, 60, 100 }.Select(k => $$"""
        rank-profile hybrid_rrf_{{k}} inherits hybrid_normalized {
            global-phase {
                expression: (1-query(vector_weight)) * reciprocal_rank(lexical_score, {{k}}) + query(vector_weight) * reciprocal_rank(semantic_score, {{k}})
                rerank-count: 200
            }
        }
        """));

    internal static string NativeSchema(int dimensions) => $$"""
        schema retrieval_evaluation {
            document retrieval_evaluation {
                field language type string { indexing: "en" | set_language }
                field run_id type string {
                    indexing: attribute | summary
                    attribute: fast-search
                    match: cased
                    rank: filter
                }
                field record_id type string {
                    indexing: attribute | summary
                    attribute: fast-search
                    match: cased
                    rank: filter
                }
                field bucket type int {
                    indexing: attribute | summary
                    attribute: fast-search
                    rank: filter
                }
                field title type string { indexing: index | summary
                    index: enable-bm25 }
                field body type string { indexing: index | summary
                    index: enable-bm25 }
                field text type string { indexing: index | summary
                    index: enable-bm25 }
                field embedding type tensor<float>(x[{{dimensions}}]) {
                    indexing: attribute | index
                    attribute { distance-metric: angular }
                    index { hnsw { max-links-per-node: 16
                        neighbors-to-explore-at-insert: 200 } }
                }
            }
            {{NativeProfiles.Replace("DIMENSIONS", dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)}}
        }
        """;

    internal static string Schema(int dimensions, int rerankCount) => $$"""
        schema retrieval_evaluation {
            document retrieval_evaluation {
                field language type string { indexing: "en" | set_language }
                field run_id type string {
                    indexing: attribute | summary
                    attribute: fast-search
                    match: cased
                    rank: filter
                }
                field text type string {
                    indexing: index | summary
                    index: enable-bm25
                }
                field embedding type tensor<float>(x[{{dimensions}}]) {
                    indexing: attribute
                    attribute { distance-metric: angular }
                }
            }
            document-summary evaluation_data {
                summary documentid {}
                summary run_id {}
                summary text {}
            }
            rank-profile lexical {
                first-phase { expression: bm25(text) }
            }
            rank-profile semantic {
                inputs { query(query_embedding) tensor<float>(x[{{dimensions}}]) }
                first-phase { expression: closeness(field, embedding) }
                match-features: distance(field, embedding)
            }
            rank-profile hybrid_normalized inherits semantic {
                first-phase { expression: log(1 + bm25(text)) + closeness(field, embedding) }
                global-phase {
                    expression: normalize_linear(bm25(text)) + normalize_linear(closeness(field, embedding))
                    rerank-count: {{rerankCount}}
                }
            }
            rank-profile hybrid_rrf inherits hybrid_normalized {
                global-phase {
                    expression: reciprocal_rank_fusion(bm25(text), closeness(field, embedding))
                    rerank-count: {{rerankCount}}
                }
            }
        }
        """;

    internal static MemoryStream Package(string schema)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add("schemas/retrieval_evaluation.sd", schema);
            Add("hosts.xml", "<hosts><host name=\"localhost\"><alias>node1</alias></host></hosts>");
            var services = """
                <services version="1.0">
                    <container id="default" version="1.0"><search/><document-api/></container>
                    <content id="content" version="1.0">
                        <redundancy>1</redundancy>
                        <documents><document type="retrieval_evaluation" mode="index"/></documents>
                        <nodes><node distribution-key="0" hostalias="node1"/></nodes>
                        <tuning><resource-limits><disk>0.95</disk></resource-limits></tuning>
                    </content>
                </services>
                """;
            Add("services.xml", services);

            void Add(string path, string text)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(text);
            }
        }
        stream.Position = 0;
        return stream;
    }
}
