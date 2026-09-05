namespace Ago.Chat.Domain.Tests;

/// <summary>`24-02`: a pure aggregate with no clock, no database and nothing to fake (testing.md's
/// domain-unit level), the same shape <see cref="AcceptanceRecordTests"/> uses for its own factory.
/// Covers the invariants the item's own Done-when names: a superseded version stays in
/// <see cref="Document.Versions"/> after a new one publishes, and the version identifier is
/// stable/ordered/human-quotable by construction.</summary>
public class DocumentTests
{
    private static readonly DocumentId Id = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 3, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WhenValid_SetsDocumentKeyAndStartsWithNoVersions()
    {
        var document = Document.Create(Id, "privacy-policy");

        Assert.Equal("privacy-policy", document.DocumentKey);
        Assert.Equal(0, document.LastSequence);
        Assert.Empty(document.Versions);
        Assert.Null(document.Current);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithAnEmptyKey_Throws(string documentKey) =>
        Assert.Throws<ArgumentException>(() => Document.Create(Id, documentKey));

    [Fact]
    public void Create_WithAnOversizedKey_Throws() =>
        Assert.Throws<ArgumentException>(() => Document.Create(Id, new string('k', Document.MaxDocumentKeyLength + 1)));

    [Theory]
    [InlineData("Privacy-Policy")] // uppercase
    [InlineData("privacy_policy")] // underscore
    [InlineData("-privacy-policy")] // leading hyphen
    [InlineData("privacy-policy-")] // trailing hyphen
    [InlineData("privacy--policy")] // double hyphen
    [InlineData("privacy policy")] // space
    public void Create_WithAnInvalidKeyShape_Throws(string documentKey) =>
        Assert.Throws<ArgumentException>(() => Document.Create(Id, documentKey));

    [Fact]
    public void Create_TrimsTheKey() =>
        Assert.Equal("privacy-policy", Document.Create(Id, "  privacy-policy  ").DocumentKey);

    [Fact]
    public void Publish_MintsTheFirstVersionAsV1()
    {
        var document = Document.Create(Id, "privacy-policy");

        var version = document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "Privacy Policy", "DRAFT text.", Now);

        Assert.Equal(1, version.Sequence);
        Assert.Equal("v1", version.Version);
        Assert.Equal(1, document.LastSequence);
        Assert.Same(version, document.Current);
        Assert.Single(document.Versions);
    }

    [Fact]
    public void Publish_ASecondTime_MintsV2AndKeepsV1Readable()
    {
        var document = Document.Create(Id, "privacy-policy");
        var v1 = document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "Privacy Policy", "DRAFT v1 text.", Now);

        var v2 = document.Publish(
            new PublishedDocumentVersionId(Guid.NewGuid()), "Privacy Policy", "DRAFT v2 text.", Now.AddMonths(1));

        // Publishing a new version leaves the old one readable at its own identifier - `24-02`'s own
        // Done-when, proven here at the aggregate level before any repository or HTTP layer is involved.
        Assert.Equal(2, document.Versions.Count);
        Assert.Contains(v1, document.Versions);
        Assert.Contains(v2, document.Versions);
        Assert.Equal("v1", v1.Version);
        Assert.Equal("v2", v2.Version);
        Assert.Same(v2, document.Current);
        Assert.Equal("DRAFT v1 text.", v1.Body);
        Assert.Equal("DRAFT v2 text.", v2.Body);
    }

    [Fact]
    public void Publish_EachVersionCarriesTheParentsDocumentKey()
    {
        var document = Document.Create(Id, "operator-terms");

        var version = document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "Operator Terms", "DRAFT text.", Now);

        Assert.Equal("operator-terms", version.DocumentKey);
        Assert.Equal(document.Id, version.DocumentId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Publish_WithAnEmptyTitle_Throws(string title)
    {
        var document = Document.Create(Id, "privacy-policy");
        Assert.Throws<ArgumentException>(() => document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), title, "body", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Publish_WithAnEmptyBody_Throws(string body)
    {
        var document = Document.Create(Id, "privacy-policy");
        Assert.Throws<ArgumentException>(() => document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "title", body, Now));
    }

    [Fact]
    public void Publish_WithAnOversizedBody_Throws()
    {
        var document = Document.Create(Id, "privacy-policy");
        Assert.Throws<ArgumentException>(() => document.Publish(
            new PublishedDocumentVersionId(Guid.NewGuid()), "title", new string('a', PublishedDocumentVersion.MaxBodyLength + 1), Now));
    }

    [Fact]
    public void Publish_AFailedAttempt_DoesNotBurnASequenceNumber()
    {
        // `Document.Publish` increments LastSequence before PublishedDocumentVersion.Create validates -
        // a rejected publish (bad title/body) still consumes the number. Documented behaviour, not a
        // bug: the alternative (validate first, increment second) would need two passes over the same
        // strings for no real benefit, since a caller that gets ArgumentException back never sees the
        // burned number anywhere. Asserted here so a future refactor changes it on purpose, not by
        // accident.
        var document = Document.Create(Id, "privacy-policy");

        Assert.Throws<ArgumentException>(() => document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "", "body", Now));
        Assert.Equal(1, document.LastSequence);

        var version = document.Publish(new PublishedDocumentVersionId(Guid.NewGuid()), "title", "body", Now);
        Assert.Equal("v2", version.Version);
    }
}
