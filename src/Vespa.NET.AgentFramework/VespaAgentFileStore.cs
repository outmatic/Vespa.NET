#pragma warning disable MAAI001 // AgentFileStore is an experimental Microsoft contract.
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using Vespa.Models;

namespace Vespa.AgentFramework;

/// <summary>A bounded, versioned file tree for FileMemoryProvider and HarnessAgent.</summary>
public sealed class VespaAgentFileStore : AgentFileStore
{
    // Match Microsoft's AgentFileStore search budgets (SDK 1.26.0).
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);
    private readonly VespaAgentStateStore _store;
    private readonly string _key;

    public VespaAgentFileStore(VespaAgentStateStore store, string rootKey = "file-memory")
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKey);
        _store = store;
        _key = "files/" + rootKey;
    }

    public override Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        path = Normalize(path, false);
        ArgumentNullException.ThrowIfNull(content);
        return MutateAsync(tree =>
        {
            if (tree.Directories.Contains(path))
                throw new IOException("A directory already exists at this path.");
            if (tree.Files.TryGetValue(path, out var existing) && string.Equals(existing, content, StringComparison.Ordinal))
                return false;
            AddParents(tree, path);
            tree.Files[path] = content;
            return true;
        }, cancellationToken);
    }

    public override async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Normalize(path, false);
        return (await ReadTreeAsync(cancellationToken)).Files.GetValueOrDefault(path);
    }

    public override async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Normalize(path, false);
        return await MutateAsync(tree => tree.Files.Remove(path), cancellationToken);
    }

    public override async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default) =>
        await ReadAsync(path, cancellationToken) is not null;

    public override async Task<IReadOnlyList<FileSearchResult>> SearchAsync(string directory, string regexPattern,
        string? globPattern = null, bool recursive = false, CancellationToken cancellationToken = default)
    {
        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase, RegexTimeout);
        directory = Normalize(directory, true);
        var prefix = directory.Length == 0 ? "" : directory + "/";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SearchTimeout);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            // All file contents already live in this snapshot. Avoid a full-tree GET per file/directory.
            var tree = await ReadTreeAsync(timeout.Token).WaitAsync(timeout.Token);
            Matcher? matcher = globPattern is null ? null : new Matcher(StringComparison.OrdinalIgnoreCase).AddInclude(globPattern);
            List<FileSearchResult> results = [];
            foreach (var (path, content) in tree.Files)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!path.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var name = path[prefix.Length..];
                if ((!recursive && name.Contains('/', StringComparison.Ordinal)) ||
                    matcher is not null && !matcher.Match(name).HasMatches)
                    continue;
                if (ScanContent(name, content, regex, timeout.Token) is { } result)
                    results.Add(result);
            }
            timeout.Token.ThrowIfCancellationRequested();
            return results;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new RegexMatchTimeoutException(string.Empty, regexPattern, SearchTimeout);
        }
    }

    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(string directory, CancellationToken cancellationToken = default)
    {
        directory = Normalize(directory, true);
        var prefix = directory.Length == 0 ? "" : directory + "/";
        var tree = await ReadTreeAsync(cancellationToken);
        return [.. tree.Directories.Where(d => DirectChild(d, prefix)).Order(StringComparer.Ordinal)
                .Select(d => new FileStoreEntry(d[prefix.Length..], FileStoreEntry.Directory)),
            .. tree.Files.Keys.Where(f => DirectChild(f, prefix)).Order(StringComparer.Ordinal)
                .Select(f => new FileStoreEntry(f[prefix.Length..], FileStoreEntry.File))];
    }

    public override async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        path = Normalize(path, true);
        if (path.Length == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return;
        }
        await MutateAsync(tree =>
        {
            if (tree.Files.ContainsKey(path))
                throw new IOException("A file already exists at this path.");
            AddParents(tree, path);
            return tree.Directories.Add(path);
        }, cancellationToken);
    }

    private async Task<FileTree> ReadTreeAsync(CancellationToken cancellationToken) =>
        Tree(await _store.ReadAsync(_key, cancellationToken));

    private async Task<bool> MutateAsync(Func<FileTree, bool> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var snapshot = await _store.ReadAsync(_key, cancellationToken);
            var tree = Tree(snapshot);
            if (!change(tree))
                return false;
            try
            {
                await _store.WriteAsync(_key, JsonSerializer.SerializeToElement(tree), snapshot?.Revision, cancellationToken);
                return true;
            }
            catch (VespaConditionNotMetException) when (attempt < 4)
            {
                // Retry this single file operation against the latest tree, preserving other files.
            }
        }
    }

    private static FileTree Tree(VespaAgentStateSnapshot? snapshot) => snapshot is null ? new()
        : snapshot.Payload.Deserialize<FileTree>() ?? throw new JsonException("The file-memory snapshot is missing.");

    private static bool DirectChild(string path, string prefix) => path.StartsWith(prefix, StringComparison.Ordinal)
        && path.Length > prefix.Length && !path[prefix.Length..].Contains('/', StringComparison.Ordinal);

    private static void AddParents(FileTree tree, string path)
    {
        for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash >= 0; slash = path.IndexOf('/', slash + 1))
        {
            var parent = path[..slash];
            if (tree.Files.ContainsKey(parent))
                throw new IOException("A parent path is a file.");
            tree.Directories.Add(parent);
        }
    }

    private static string Normalize(string path, bool directory)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':', StringComparison.Ordinal) || path.Any(char.IsControl))
            throw new ArgumentException("Use a relative file-memory path.", nameof(path));
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or "..") || (!directory && (parts.Length == 0 || path.EndsWith('/') || path.EndsWith('\\'))))
            throw new ArgumentException("File-memory paths cannot traverse directories or name the root as a file.", nameof(path));
        return string.Join('/', parts).ToLowerInvariant();
    }

    private sealed class FileTree
    {
        public Dictionary<string, string> Files { get; set; } = [];
        public HashSet<string> Directories { get; set; } = [];
    }
}
