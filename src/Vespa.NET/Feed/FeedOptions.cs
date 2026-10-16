namespace Vespa.Feed;

/// <summary>Settings for a mixed feed of put, update and delete operations.</summary>
public sealed record FeedOptions
{
    /// <summary>Maximum number of operations executing concurrently (default 64).</summary>
    public int MaxConcurrency { get; init; } = 64;

    /// <summary>Maximum number of queued operations waiting to execute (default 256).</summary>
    public int BoundedCapacity { get; init; } = 256;

    /// <summary>
    /// Optional progress callback. Invoked concurrently from workers after each
    /// success or failure; a throwing callback faults the pipeline.
    /// </summary>
    public Action<FeedProgress>? OnProgress { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BoundedCapacity);
    }
}
