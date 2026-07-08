using Vespa.Models;
using Xunit;

namespace Vespa.Tests.Unit;

/// <summary>
/// Tests for the <see cref="VespaDocumentId"/> grammar helpers — the structural
/// parse behind full-ID routing and namespace/type mismatch validation.
/// </summary>
public class VespaDocumentIdTests
{
    // ── TryParse: valid full IDs ─────────────────────────────────────────────

    [Fact]
    public void TryParse_PlainFullId_ParsesAllSegments()
    {
        Assert.True(VespaDocumentId.TryParse("id:myns:music::doc-1", out var parts));
        Assert.Equal("myns", parts.Namespace);
        Assert.Equal("music", parts.DocumentType);
        Assert.Null(parts.Group);
        Assert.Null(parts.Number);
        Assert.Equal("doc-1", parts.UserSpecified);
    }

    [Fact]
    public void TryParse_UserPartWithColons_KeepsColons()
    {
        Assert.True(VespaDocumentId.TryParse("id:ns:type::album::dark-side", out var parts));
        Assert.Equal("album::dark-side", parts.UserSpecified);
    }

    [Fact]
    public void TryParse_GroupSelector_ParsesGroup()
    {
        Assert.True(VespaDocumentId.TryParse("id:ns:type:g=alice:doc1", out var parts));
        Assert.Equal("alice", parts.Group);
        Assert.Null(parts.Number);
        Assert.Equal("doc1", parts.UserSpecified);
    }

    [Fact]
    public void TryParse_NumberSelector_ParsesNumber()
    {
        Assert.True(VespaDocumentId.TryParse("id:ns:type:n=1234:doc1", out var parts));
        Assert.Equal(1234L, parts.Number);
        Assert.Null(parts.Group);
    }

    [Fact]
    public void TryParse_GroupValueContainingEquals_IsPreserved()
    {
        Assert.True(VespaDocumentId.TryParse("id:ns:type:g=a=b:doc1", out var parts));
        Assert.Equal("a=b", parts.Group);
    }

    // ── TryParse: rejected inputs ────────────────────────────────────────────

    [Theory]
    [InlineData("doc-1")]                    // bare ID
    [InlineData("a::b")]                     // bare ID with colons
    [InlineData("id:onlythree:segments")]    // too few segments
    [InlineData("id::type::user")]           // empty namespace
    [InlineData("id:ns:::user")]             // empty document type
    [InlineData("id:ns:type:bogus:user")]    // invalid key/value pair
    [InlineData("id:ns:type:n=abc:user")]    // non-numeric n=
    [InlineData("id:ns:type:g=:user")]       // empty group
    [InlineData("")]
    public void TryParse_InvalidOrBareIds_ReturnsFalse(string id)
    {
        Assert.False(VespaDocumentId.TryParse(id, out var parts));
        Assert.Null(parts);
    }

    // ── GetUserSpecified (unchanged behavior) ────────────────────────────────

    [Theory]
    [InlineData("id:ns:type::doc-1", "doc-1")]
    [InlineData("id:ns:type:g=alice:doc:1", "doc:1")]
    [InlineData("doc-1", "doc-1")]
    [InlineData("a::b", "a::b")]
    [InlineData("group:root:0", "group:root:0")]
    public void GetUserSpecified_ReturnsUserPartOrInput(string id, string expected)
        => Assert.Equal(expected, VespaDocumentId.GetUserSpecified(id));
}
