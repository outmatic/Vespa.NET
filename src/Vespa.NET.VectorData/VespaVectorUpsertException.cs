using Microsoft.Extensions.VectorData;

namespace Vespa.VectorData;

/// <summary>Acknowledgement state of one input record; an unconfirmed write may already have reached Vespa.</summary>
public enum VespaVectorUpsertStatus
{
    NotAttempted,
    Succeeded,
    Unconfirmed
}

/// <summary>Outcome for an enumerated input record. Index preserves duplicates and input order; Key is null before key validation.</summary>
public sealed record VespaVectorUpsertOutcome(long Index, string? Key, VespaVectorUpsertStatus Status, Exception? Error = null);

/// <summary>A non-transactional upsert stopped. Outcomes describe enumerated records, not any remaining input.</summary>
public sealed class VespaVectorUpsertException : VectorStoreException
{
    internal VespaVectorUpsertException(string collection, IReadOnlyList<VespaVectorUpsertOutcome> outcomes,
        bool inputFullyEnumerated, Exception innerException) : base(
            $"Upsert stopped: {innerException.Message} Writes may have been partially applied; inspect Outcomes before retrying.",
            innerException is VectorStoreException { InnerException: { } cause } ? cause : innerException)
    {
        VectorStoreSystemName = "vespa";
        CollectionName = collection;
        OperationName = "UpsertAsync";
        Outcomes = Array.AsReadOnly(outcomes.ToArray());
        InputFullyEnumerated = inputFullyEnumerated;
    }

    public IReadOnlyList<VespaVectorUpsertOutcome> Outcomes { get; }
    public bool InputFullyEnumerated { get; }
}

/// <summary>Caller cancellation with acknowledgement information for any enumerated records.</summary>
public sealed class VespaVectorUpsertCanceledException : OperationCanceledException
{
    internal VespaVectorUpsertCanceledException(IReadOnlyList<VespaVectorUpsertOutcome> outcomes,
        bool inputFullyEnumerated, OperationCanceledException innerException, CancellationToken cancellationToken)
        : base("Upsert was cancelled. Some writes may already have been applied; inspect Outcomes before retrying.", innerException, cancellationToken)
    {
        Outcomes = Array.AsReadOnly(outcomes.ToArray());
        InputFullyEnumerated = inputFullyEnumerated;
    }

    public IReadOnlyList<VespaVectorUpsertOutcome> Outcomes { get; }
    public bool InputFullyEnumerated { get; }
}
