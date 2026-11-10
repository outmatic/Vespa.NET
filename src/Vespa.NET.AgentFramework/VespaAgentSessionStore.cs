#pragma warning disable MAAI001 // AgentSessionStore is an experimental Microsoft contract.
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Vespa.AgentFramework;

/// <summary>Persists complete serialized sessions, including provider state and service conversation IDs.</summary>
public sealed class VespaAgentSessionStore(VespaAgentStateStore store) : AgentSessionStore
{
    private readonly VespaAgentStateStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly ConditionalWeakTable<AgentSession, SessionVersion> _versions = new();

    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key,
        CancellationToken cancellationToken = default)
    {
        var documentKey = Key(agent, key);
        var snapshot = await _store.ReadAsync(documentKey, cancellationToken);
        if (snapshot is null)
            return null;
        var session = await agent.DeserializeSessionAsync(snapshot.Payload, cancellationToken: cancellationToken);
        _versions.Add(session, new() { Key = documentKey, Revision = snapshot.Revision });
        return session;
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var documentKey = Key(agent, key);
        var version = _versions.GetOrCreateValue(session);
        if (version.Key is not null && version.Key != documentKey)
            throw new ArgumentException("A loaded session cannot be saved under a different store key.", nameof(key));
        var json = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
        var saved = await _store.WriteAsync(documentKey, json, version.Revision, cancellationToken);
        version.Key = documentKey;
        version.Revision = saved.Revision;
    }

    public override async ValueTask DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey key, CancellationToken cancellationToken = default)
    {
        var documentKey = Key(agent, key);
        if (await _store.ReadAsync(documentKey, cancellationToken) is { } snapshot)
            await _store.DeleteAsync(documentKey, snapshot.Revision, cancellationToken);
    }

    private static string Key(AIAgent agent, AgentSessionStoreKey key)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent.Name);
        return "session/" + JsonSerializer.Serialize(new
        {
            Agent = agent.Name,
            key.SessionId,
            Partitions = key.Partitions?.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new[] { p.Key, p.Value }).ToArray()
        });
    }

    private sealed class SessionVersion
    {
        public string? Key { get; set; }
        public string? Revision { get; set; }
    }
}
