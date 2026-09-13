using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-69`/`23-77`'s own direct proof against a real Postgres (<see cref="PostgresFixture"/>): a
/// restriction actually blocks <see cref="VisitorRestrictionRepository.IsActiveAsync"/>'s own answer,
/// a natural expiry lifts it with no explicit act (both items' own Done-when: "after the expiry, the
/// restriction lifts on its own"), and an early <c>LiftAsync</c> lifts it before that (the reversibility
/// both items' own Scope sections require).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VisitorRestrictionRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IsActiveAsync_TrueImmediatelyAfterRestrictAsync_ForATimeWindowedMute()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitorAsync();
        var repository = new VisitorRestrictionRepository(fixture.DataSource);
        var conversationId = new ConversationId(Guid.NewGuid());

        await repository.RestrictAsync(
            siteId, visitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Spam, Now.AddHours(24),
            conversationId, Guid.NewGuid(), Now, CancellationToken.None);

        Assert.True(await repository.IsActiveAsync(siteId, visitorId, Now.AddMinutes(1), CancellationToken.None));
    }

    [Fact]
    public async Task IsActiveAsync_FalseOnceItsOwnExpiryHasPassed_WithNoExplicitLift()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitorAsync();
        var repository = new VisitorRestrictionRepository(fixture.DataSource);
        var expiresAt = Now.AddHours(24);

        await repository.RestrictAsync(
            siteId, visitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Spam, expiresAt,
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);

        Assert.True(await repository.IsActiveAsync(siteId, visitorId, expiresAt.AddSeconds(-1), CancellationToken.None));
        Assert.False(await repository.IsActiveAsync(siteId, visitorId, expiresAt.AddSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task IsActiveAsync_TrueIndefinitely_ForABlockWithNoExpiry()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitorAsync();
        var repository = new VisitorRestrictionRepository(fixture.DataSource);

        await repository.RestrictAsync(
            siteId, visitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Block, expiresAt: null,
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);

        Assert.True(await repository.IsActiveAsync(siteId, visitorId, Now.AddYears(10), CancellationToken.None));
    }

    [Fact]
    public async Task LiftAsync_StopsAnActiveRestrictionBeforeItsOwnNaturalExpiry_AndIsRecorded()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitorAsync();
        var repository = new VisitorRestrictionRepository(fixture.DataSource);
        var liftedBy = new OperatorId(Guid.NewGuid());

        await repository.RestrictAsync(
            siteId, visitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Spam, Now.AddHours(24),
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);

        var lifted = await repository.LiftAsync(siteId, visitorId, liftedBy, Now.AddMinutes(5), CancellationToken.None);

        Assert.True(lifted);
        Assert.False(await repository.IsActiveAsync(siteId, visitorId, Now.AddMinutes(6), CancellationToken.None));

        var page = await repository.ListForSiteAsync(siteId, null, 10, CancellationToken.None);
        var row = Assert.Single(page.Items);
        Assert.Equal(Now.AddMinutes(5), row.LiftedAt);
        Assert.Equal(liftedBy, row.LiftedBy);
    }

    [Fact]
    public async Task LiftAsync_ReturnsFalse_WhenNothingIsCurrentlyActive()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitorAsync();
        var repository = new VisitorRestrictionRepository(fixture.DataSource);

        var lifted = await repository.LiftAsync(siteId, visitorId, new OperatorId(Guid.NewGuid()), Now, CancellationToken.None);

        Assert.False(lifted);
    }

    [Fact]
    public async Task GetActiveKindAsync_PrefersBlock_WhenBothKindsAreSomehowActiveAtOnce()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitorAsync();
        var repository = new VisitorRestrictionRepository(fixture.DataSource);

        await repository.RestrictAsync(
            siteId, visitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Spam, Now.AddHours(24),
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        await repository.RestrictAsync(
            siteId, visitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Block, expiresAt: null,
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);

        var kind = await repository.GetActiveKindAsync(siteId, visitorId, Now.AddMinutes(1), CancellationToken.None);

        Assert.Equal(VisitorRestrictionKind.Block, kind);
    }

    [Fact]
    public async Task ListForSiteAsync_IsScopedToOneSite_NewestFirst()
    {
        var (siteId, visitorId) = await SeedSiteAndVisitorAsync();
        var (otherSiteId, otherVisitorId) = await SeedSiteAndVisitorAsync();
        var repository = new VisitorRestrictionRepository(fixture.DataSource);
        // `ListForSiteAsync` keysets by `id desc`, relying on every real caller minting ids through
        // `IIdGenerator` (time-sortable UUIDv7) - a bare `Guid.NewGuid()` here would make "newest
        // first" an assertion about random bytes rather than insertion order, so this one test uses the
        // real generator instead, the same way every production caller does.
        var idGenerator = new UuidV7Generator();

        await repository.RestrictAsync(
            siteId, visitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Spam, Now.AddHours(24),
            new ConversationId(Guid.NewGuid()), idGenerator.NewId(Now), Now, CancellationToken.None);
        await repository.RestrictAsync(
            siteId, visitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Block, expiresAt: null,
            new ConversationId(Guid.NewGuid()), idGenerator.NewId(Now.AddSeconds(1)), Now.AddSeconds(1), CancellationToken.None);
        await repository.RestrictAsync(
            otherSiteId, otherVisitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Block, expiresAt: null,
            new ConversationId(Guid.NewGuid()), idGenerator.NewId(Now), Now, CancellationToken.None);

        var page = await repository.ListForSiteAsync(siteId, null, 10, CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(VisitorRestrictionKind.Block, page.Items[0].Kind);
        Assert.Equal(VisitorRestrictionKind.Spam, page.Items[1].Kind);
        Assert.Null(page.NextBeforeId);
    }

    private async Task<(SiteId SiteId, VisitorId VisitorId)> SeedSiteAndVisitorAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        await db.SaveChangesAsync();
        return (siteId, visitorId);
    }
}
