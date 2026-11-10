using System.IO.Compression;

namespace Vespa.VectorData.Benchmarks;

internal static class BenchmarkApplication
{
    internal static string Schema(int dimensions) => $$"""
        schema vector_benchmark {
            document vector_benchmark {
                field run_id type string {
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
                field text type string { indexing: summary }
                field embedding type tensor<float>(x[{{dimensions}}]) {
                    indexing: attribute | index | summary
                    attribute { distance-metric: angular }
                    index {
                        hnsw {
                            max-links-per-node: 16
                            neighbors-to-explore-at-insert: 200
                        }
                    }
                }
            }
            document-summary vector_data {
                from-disk
                summary documentid {}
                summary run_id {}
                summary bucket {}
                summary text {}
            }
            rank-profile vector_search {
                inputs { query(query_embedding) tensor<float>(x[{{dimensions}}]) }
                first-phase { expression: closeness(field, embedding) }
                match-features: distance(field, embedding)
            }
        }
        """;

    internal static MemoryStream Package(string schema)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add("schemas/vector_benchmark.sd", schema);
            Add("hosts.xml", """
                <hosts><host name="localhost"><alias>node1</alias></host></hosts>
                """);
            Add("services.xml", """
                <services version="1.0">
                    <container id="default" version="1.0"><search/><document-api/></container>
                    <content id="content" version="1.0">
                        <redundancy>1</redundancy>
                        <documents><document type="vector_benchmark" mode="index"/></documents>
                        <nodes><node distribution-key="0" hostalias="node1"/></nodes>
                        <tuning><resource-limits><disk>0.95</disk></resource-limits></tuning>
                    </content>
                </services>
                """);

            void Add(string path, string content)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                writer.Write(content);
            }
        }
        stream.Position = 0;
        return stream;
    }
}
