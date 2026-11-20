using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vespa.RetrievalEvaluation;

internal static class EvaluationScope
{
    internal static int Bucket(string id) => (int)(BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(id))) % 100);

    internal static string Filter(string runId, int scopePercent, EvaluationQuery query)
    {
        var filter = $"run_id contains \"{runId}\"";
        if (scopePercent < 100)
            filter += " and bucket < " + scopePercent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (query.ExcludeDocumentId is { } excluded)
            filter += " and !(record_id contains " + JsonSerializer.Serialize(excluded) + ")";
        return filter;
    }
}
