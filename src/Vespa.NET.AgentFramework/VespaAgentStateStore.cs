using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vespa.Models;

namespace Vespa.AgentFramework;

/// <summary>Versioned JSON snapshots stored through Vespa's document API.</summary>
public sealed class VespaAgentStateStore
{
    private readonly IVespaClient _client;
    private readonly VespaAgentStateStoreOptions _options;

    public VespaAgentStateStore(IVespaClient client, VespaAgentStateStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Schema);
        if (options.Namespace.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("A namespace cannot contain ':'.", nameof(options));
        if (!(char.IsAsciiLetter(options.Schema[0]) || options.Schema[0] == '_') ||
            options.Schema.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Schema must be a simple Vespa identifier.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPayloadBytes);
        _client = client;
        _options = options;
    }

    public async Task<VespaAgentStateSnapshot?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = await _client.Documents.GetAsync<Dictionary<string, JsonElement>>(
            DocumentKey(key), _options.Schema, _options.Namespace, cancellationToken: cancellationToken);
        if (document is null)
            return null;
        var fields = document.Fields ?? throw new JsonException("The agent-state document has no fields.");
        if (fields["format_version"].GetInt32() != 1 || fields["scope"].GetString() != _options.Scope || fields["key"].GetString() != key)
            throw new JsonException("The agent-state document has an incompatible format or identity.");
        var revision = fields["revision"].GetString()!;
        ValidateRevision(revision);
        using var payload = JsonDocument.Parse(fields["payload"].GetString()!);
        return new(payload.RootElement.Clone(), revision);
    }

    /// <summary>Creates a snapshot when expectedRevision is null; otherwise replaces exactly that revision.</summary>
    public async Task<VespaAgentStateSnapshot> WriteAsync(string key, JsonElement payload, string? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = DocumentKey(key);
        if (expectedRevision is not null)
            ValidateRevision(expectedRevision);
        var json = payload.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > _options.MaxPayloadBytes)
            throw new ArgumentException($"The snapshot exceeds {_options.MaxPayloadBytes} UTF-8 bytes. Archive or reduce stored data before writing.", nameof(payload));
        var revision = Guid.NewGuid().ToString("D");
        Dictionary<string, object> fields = new()
        {
            ["format_version"] = 1,
            ["scope"] = _options.Scope,
            ["key"] = key,
            ["revision"] = revision,
            ["payload"] = json
        };
        try
        {
            var response = await _client.Documents.PutAsync(id, fields, _options.Schema, _options.Namespace,
                condition: Condition(expectedRevision ?? ""), create: expectedRevision is null, cancellationToken: cancellationToken);
            if (response.HadIgnoredFields)
                throw new InvalidOperationException("Vespa ignored agent-state fields. Check the deployed schema; the write may have been partially applied.");
        }
        catch (VespaConditionNotMetException)
        {
            // A transport retry can observe a failed condition after the first PUT succeeded.
            if ((await ReadAsync(key, cancellationToken))?.Revision != revision)
                throw;
        }
        return new(payload.Clone(), revision);
    }

    public async Task DeleteAsync(string key, string expectedRevision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRevision(expectedRevision);
        await _client.Documents.DeleteAsync(DocumentKey(key), _options.Schema, _options.Namespace,
            condition: Condition(expectedRevision), cancellationToken: cancellationToken);
    }

    private string DocumentKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { _options.Scope, key }))).ToLowerInvariant();
    }

    private string Condition(string revision) => $"{_options.Schema}.revision == \"{revision}\"";

    private static void ValidateRevision(string revision)
    {
        if (!Guid.TryParseExact(revision, "D", out var parsed) || parsed.ToString("D") != revision)
            throw new ArgumentException("Expected a canonical snapshot revision.", nameof(revision));
    }
}

public sealed record VespaAgentStateStoreOptions
{
    public required string Namespace { get; init; }
    /// <summary>A trusted application scope, such as a tenant/user/agent identity. This is not authorization.</summary>
    public required string Scope { get; init; }
    public string Schema { get; init; } = "agentstate";
    public int MaxPayloadBytes { get; init; } = 4 * 1024 * 1024;
}

public sealed record VespaAgentStateSnapshot(JsonElement Payload, string Revision);
