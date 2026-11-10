#pragma warning disable MAAI001 // Exercises the published AgentFileStore contract.
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Xunit;

namespace Vespa.AgentFramework.Tests;

public sealed class FileSearchTests
{
    [Fact]
    public async Task IdenticalFileWrite_DoesNotWriteOrAdvanceTheSnapshotRevision()
    {
        using var fixture = new StateFixture();
        var files = new VespaAgentFileStore(fixture.Store);
        await files.WriteAsync("notes/preference.md", "Use C#.");
        var snapshot = await fixture.Store.ReadAsync("files/file-memory");
        var puts = fixture.Backend.PutCount;
        var gets = fixture.Backend.GetCount;

        await files.WriteAsync("NOTES/PREFERENCE.MD", "Use C#.");

        Assert.Equal(gets + 1, fixture.Backend.GetCount);
        Assert.Equal(puts, fixture.Backend.PutCount);
        Assert.Equal(snapshot!.Revision, (await fixture.Store.ReadAsync("files/file-memory"))!.Revision);
        await files.WriteAsync("notes/preference.md", "Use F#.");
        Assert.Equal(puts + 1, fixture.Backend.PutCount);
        Assert.Equal("Use F#.", await files.ReadAsync("notes/preference.md"));
    }

    [Fact]
    public async Task Search_ReadsOneSnapshotForTheWholeTree()
    {
        using var fixture = new StateFixture();
        var files = new VespaAgentFileStore(fixture.Store);
        for (var i = 0; i < 20; i++)
            await files.WriteAsync($"notes/folder-{i}/note.md", "target");
        var reads = fixture.Backend.GetCount;

        var matches = await files.SearchAsync("notes", "TARGET", recursive: true);

        Assert.Equal(20, matches.Count);
        Assert.Equal(reads + 1, fixture.Backend.GetCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("*.md", false)]
    [InlineData("*.md", true)]
    [InlineData("**/*.MD", true)]
    [InlineData("nested/*.md", true)]
    [InlineData("nested/*.md", false)]
    [InlineData("*.txt", true)]
    public async Task Search_MatchesMicrosoftGlobScopeAndPhysicalLineSemantics(string? glob, bool recursive)
    {
        using var fixture = new StateFixture();
        var files = new VespaAgentFileStore(fixture.Store);
        var reference = new InMemoryAgentFileStore();
        (string Path, string Content)[] entries =
        [
            ("notes/first.md", "one\r\nTarget\rthree\nfour"),
            ("notes/second.txt", "target\n"),
            ("notes/nested/third.md", "target\r\nlast"),
            ("notes/nested/deep/fourth.md", "target"),
            ("notes-other/outside.md", "target"),
            ("outside.md", "target")
        ];
        foreach (var (path, content) in entries)
        {
            await files.WriteAsync(path, content);
            await reference.WriteAsync(path, content);
        }
        var expected = await reference.SearchAsync("notes", "^target$", glob, recursive);
        var actual = await files.SearchAsync("NOTES/", "^target$", glob, recursive);

        Assert.Equal(JsonSerializer.Serialize(expected.OrderBy(r => r.FileName)),
            JsonSerializer.Serialize(actual.OrderBy(r => r.FileName)));
        foreach (var match in actual)
        {
            var content = await files.ReadAsync("notes/" + match.FileName);
            var lines = AgentFileStore.SplitLines(content!);
            foreach (var line in match.MatchingLines)
                Assert.Equal(lines[line.LineNumber - 1], line.Line);
        }
    }

    [Fact]
    public async Task Search_CancelledBeforeReading_PerformsNoIo()
    {
        using var fixture = new StateFixture();
        var files = new VespaAgentFileStore(fixture.Store);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => files.SearchAsync("", "target", cancellationToken: cancelled.Token));
        Assert.Equal(0, fixture.Backend.GetCount);
    }

    [Fact]
    public async Task Search_CallerCancellationStopsWaitingForAnUncooperativeRead()
    {
        using var fixture = new StateFixture();
        var files = new VespaAgentFileStore(fixture.Store);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.BeforeGetAsync = _ =>
        {
            entered.SetResult();
            return release.Task;
        };
        try
        {
            var search = files.SearchAsync("", "target", cancellationToken: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            release.SetResult();
        }
    }

    [Fact]
    public async Task Search_OverallTimeoutMatchesMicrosoftAndDoesNotWaitForTheRead()
    {
        using var fixture = new StateFixture();
        var files = new VespaAgentFileStore(fixture.Store);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.BeforeGetAsync = _ => release.Task;
        try
        {
            var error = await Assert.ThrowsAsync<RegexMatchTimeoutException>(() =>
                files.SearchAsync("", "target").WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal(TimeSpan.FromSeconds(10), error.MatchTimeout);
            Assert.Equal("target", error.Pattern);
        }
        finally
        {
            release.SetResult();
        }
    }

    [Fact]
    public async Task Search_PathologicalRegexHasMicrosoftsPerMatchBudget()
    {
        using var fixture = new StateFixture();
        var files = new VespaAgentFileStore(fixture.Store);
        await files.WriteAsync("note.md", new string('a', 30000) + "!");

        var error = await Assert.ThrowsAsync<RegexMatchTimeoutException>(() => files.SearchAsync("", "^(a+)+$")
            .WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.Equal(TimeSpan.FromSeconds(5), error.MatchTimeout);
    }

    [Fact]
    public async Task Search_InvalidRegexFailsBeforeReading()
    {
        using var fixture = new StateFixture();
        var files = new VespaAgentFileStore(fixture.Store);
        await Assert.ThrowsAsync<RegexParseException>(() => files.SearchAsync("", "["));
        Assert.Equal(0, fixture.Backend.GetCount);
    }
}
