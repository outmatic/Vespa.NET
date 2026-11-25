namespace Vespa.Models.Schema;

internal sealed record RankFunctionModel(string Signature, string Expression);

internal sealed record RankProfileDefinition(
    string Name,
    string? Inherits,
    string? FirstPhase,
    string? SecondPhase,
    int SecondPhaseRerankCount,
    string? GlobalPhase,
    int GlobalPhaseRerankCount,
    IReadOnlyList<RankFunctionModel> Functions);
