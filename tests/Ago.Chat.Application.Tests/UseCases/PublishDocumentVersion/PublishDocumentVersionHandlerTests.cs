using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.PublishDocumentVersion;

namespace Ago.Chat.Application.Tests.UseCases.PublishDocumentVersion;

public class PublishDocumentVersionHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 12, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(PublishDocumentVersionHandler Handler, FakeDocumentRepository Documents, FakeCache Cache);

    private static Fixture CreateFixture(DateTimeOffset? now = null)
    {
        var documents = new FakeDocumentRepository();
        var cache = new FakeCache();
        var handler = new PublishDocumentVersionHandler(documents, new FakeIdGenerator(), new FakeClock(now ?? Now), cache);
        return new Fixture(handler, documents, cache);
    }

    [Fact]
    public async Task HandleAsync_ForANewDocumentKey_PublishesV1()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion(
                "privacy-policy", "Privacy Policy", "DRAFT text - awaiting legal review."),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error!.Value.ToString() : null);
        Assert.Equal("privacy-policy", result.Value.DocumentKey);
        Assert.Equal("v1", result.Value.Version);
        Assert.Equal(1, result.Value.Sequence);
        Assert.Equal(Now, result.Value.PublishedAt);
    }

    [Fact]
    public async Task HandleAsync_ForAnExistingDocumentKey_PublishesTheNextVersion()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion("privacy-policy", "Privacy Policy", "DRAFT v1."),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion("privacy-policy", "Privacy Policy", "DRAFT v2."),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("v2", result.Value.Version);

        var document = await fixture.Documents.GetByKeyAsync("privacy-policy", CancellationToken.None);
        Assert.Equal(2, document!.Versions.Count);
    }

    [Fact]
    public async Task HandleAsync_WithAnEmptyTitle_ReturnsInvalid_AndPublishesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion("privacy-policy", "   ", "body"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Document.Invalid", result.Error!.Value.Code);
        Assert.Null(await fixture.Documents.GetByKeyAsync("privacy-policy", CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WithAnInvalidDocumentKeyShape_ReturnsInvalid()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion("Privacy_Policy", "Privacy Policy", "body"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Document.Invalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_OnSuccess_EvictsTheCurrentCacheEntryForThatKey()
    {
        var fixture = CreateFixture();
        // Warm the "current" cache entry the way GetDocumentVersionHandler would.
        await fixture.Cache.SetAsync(
            Caching.DocumentCacheKeys.ForCurrent("privacy-policy"),
            new PublishedDocumentVersionDto("privacy-policy", "v1", 1, "Privacy Policy", "stale", Now),
            new Ago.Platform.Abstractions.CacheEntryOptions(TimeSpan.FromMinutes(5)),
            CancellationToken.None);

        await fixture.Handler.HandleAsync(
            new Application.UseCases.PublishDocumentVersion.PublishDocumentVersion("privacy-policy", "Privacy Policy", "DRAFT v2."),
            CancellationToken.None);

        // A reader must never keep seeing the stale "current" entry for the rest of its TTL window -
        // GetDocumentVersionHandler's own remarks on why this eviction exists at all.
        var stillCached = await fixture.Cache.GetAsync<PublishedDocumentVersionDto>(
            Caching.DocumentCacheKeys.ForCurrent("privacy-policy"), CancellationToken.None);
        Assert.Null(stillCached);
    }
}
