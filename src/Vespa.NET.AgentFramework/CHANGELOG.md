# Vespa.NET.AgentFramework changelog

## 1.1.0 (unreleased)

- Writing unchanged file content avoids a snapshot PUT and revision change.
- File-memory regex/glob searches read the file-tree snapshot once, retaining
  Microsoft's line coordinates, search budgets and caller cancellation behavior.
- Before-retrieval history reduction skips unchanged snapshots, including when
  reducers return the original message objects.

## 1.0.0

- Versioned JSON snapshots through Vespa's document API, with conditional writes
  and a configurable payload limit.
- `VespaChatHistoryProvider` for chronological replay, per-session state and
  optional persistent history reduction before retrieval or after storage.
- `VespaAgentSessionStore` for complete session serialization, partitioned keys,
  independent restored sessions and stale-write detection.
- `VespaAgentFileStore` for Microsoft's file-memory tools, with normalized relative
  paths, directories and regex/glob search using the framework's line coordinates.
- Examples covering sessions, custom context, text search, native semantic memory,
  file memory, compaction and Harness, with documented constraints.
- .NET 8 and .NET 10; depends on Vespa.NET and Microsoft.Agents.AI.
