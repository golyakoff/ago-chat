using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetDocumentVersion;
using Ago.Chat.Application.UseCases.PublishDocumentVersion;

namespace Ago.Chat.Application.Tests.UseCases.GetDocumentVersion;

public class GetDocumentVersionHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(GetDocumentVersionHandler Reader, PublishDocumentVersionHandler Publisher, FakeCache Cache);

    private static Fixture CreateFixture()
    {
        var documents = new FakeDocumentRepository();
        var cache = new FakeCache();
        var publisher = new PublishDocumentVersionHandler(documents, new FakeIdGenerator(), new FakeClock(Now), cache);
        var reader = new GetDocumentVersionHandler(documents, cache);
        return new Fixture(reader, publisher, cache);
    }

    [Fact]
    public async Task HandleAsync_ForAnUnknownDocumentKey_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Reader.HandleAsync(new Application.UseCases.GetDocumentVersion.GetDocumentVersion("nope", null), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Document.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithNoVersionNamed_ReturnsTheCurrentVersion()
    {
        var fixture = CreateFixture();
        await fixture.Publisher.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion("privacy-policy", "Privacy Policy", "DRAFT v1."),
            CancellationToken.None);
        await fixture.Publisher.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion("privacy-policy", "Privacy Policy", "DRAFT v2."),
            CancellationToken.None);

        var result = await fixture.Reader.HandleAsync(
            new Application.UseCases.GetDocumentVersion.GetDocumentVersion("privacy-policy", null), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("v2", result.Value.Version);
        Assert.Equal("DRAFT v2.", result.Value.Body);
    }

    [Fact]
    public async Task HandleAsync_NamingAnOlderVersion_StillReadsIt_AfterANewerOneIsPublished()
    {
        // `24-02`'s own Done-when: publishing a new version leaves the old one readable at its own
        // identifier. Proven here through the actual read handler an unauthenticated caller reaches,
        // not only the aggregate directly.
        var fixture = CreateFixture();
        await fixture.Publisher.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion("privacy-policy", "Privacy Policy", "DRAFT v1."),
            CancellationToken.None);
        await fixture.Publisher.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion("privacy-policy", "Privacy Policy", "DRAFT v2."),
            CancellationToken.None);

        var v1 = await fixture.Reader.HandleAsync(
            new Application.UseCases.GetDocumentVersion.GetDocumentVersion("privacy-policy", "v1"), CancellationToken.None);
        var current = await fixture.Reader.HandleAsync(
            new Application.UseCases.GetDocumentVersion.GetDocumentVersion("privacy-policy", null), CancellationToken.None);

        Assert.True(v1.IsSuccess);
        Assert.Equal("DRAFT v1.", v1.Value.Body);
        Assert.True(current.IsSuccess);
        Assert.Equal("DRAFT v2.", current.Value.Body);
    }

    [Fact]
    public async Task HandleAsync_ForAnUnknownVersionOfAKnownKey_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        await fixture.Publisher.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion("privacy-policy", "Privacy Policy", "DRAFT v1."),
            CancellationToken.None);

        var result = await fixture.Reader.HandleAsync(
            new Application.UseCases.GetDocumentVersion.GetDocumentVersion("privacy-policy", "v99"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Document.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_CalledTwiceForTheSameVersion_ServesTheSecondCallFromCache()
    {
        var fixture = CreateFixture();
        await fixture.Publisher.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion("privacy-policy", "Privacy Policy", "DRAFT v1."),
            CancellationToken.None);

        await fixture.Reader.HandleAsync(
            new Application.UseCases.GetDocumentVersion.GetDocumentVersion("privacy-policy", "v1"), CancellationToken.None);
        var callsAfterFirst = fixture.Cache.FactoryCalls;
        await fixture.Reader.HandleAsync(
            new Application.UseCases.GetDocumentVersion.GetDocumentVersion("privacy-policy", "v1"), CancellationToken.None);

        Assert.Equal(callsAfterFirst, fixture.Cache.FactoryCalls);
    }
}
