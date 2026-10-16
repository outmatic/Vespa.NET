using System.Runtime.CompilerServices;

namespace Vespa.Feed;

internal static class FeedEnumerableExtensions
{
#pragma warning disable CS1998 // Synchronous sources use the same async pipeline without scheduling per item.
    internal static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        this IEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
#pragma warning restore CS1998
    {
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }
}
