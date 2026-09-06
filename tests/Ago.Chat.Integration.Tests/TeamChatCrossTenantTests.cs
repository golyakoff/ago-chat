using Ago.Chat.Application.UseCases.GetTeamMessageHistory;
using Ago.Chat.Application.UseCases.SendTeamMessage;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-32`'s own Done-when: "an operator of another tenant cannot read or write a word of it -
/// asserted the way every other tenant-isolation test in this codebase is" - the same level and the
/// same "real Postgres, real handlers, no fake stands in for the boundary under test" shape
/// <see cref="CrossTenantConversationAccessTests"/> already established for the conversation side.
///
/// <para><b>Why there is no "attacker names another site's id" test here, unlike
/// <see cref="CrossTenantConversationAccessTests"/>'s <c>AssignConversation</c> case.</b> That test
/// exists because <c>AssignConversation</c> takes a <see cref="ConversationId"/> naming a row that can
/// belong to any site - the caller supplies the target, and the site comparison is what a caller could
/// otherwise defeat. <see cref="SendTeamMessage"/>/<see cref="GetTeamMessageHistory"/> take no such
/// target: <see cref="SiteId"/> is not a lookup key here, it *is* the room, and every real caller
/// (<c>OperatorHub</c>) supplies it from the operator's own token claim, never from anything the
/// operator chooses. There is no parameter for an attacker to point at another tenant's room in the
/// first place - the isolation this file proves is that the *read* side never returns another site's
/// rows even when two sites' data sits in the same table, which is the genuine remaining way this
/// guarantee could fail (a `WHERE site_id = ...` clause quietly dropped, a join that forgets it).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class TeamChatCrossTenantTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private sealed record Tenant(SiteId SiteId, OperatorId OperatorId);

    [Fact]
    public async Task AnOperatorOfOneSite_NeverSeesAnotherSitesTeamMessages()
    {
        var siteA = await SeedTenantAsync();
        var siteB = await SeedTenantAsync();

        await using var db = fixture.CreateDbContext();
        var sendHandler = new SendTeamMessageHandler(
            new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator()),
            new PermissionChecker(db), new SystemClock(), new UuidV7Generator());

        var sent = await sendHandler.HandleAsync(
            new Application.UseCases.SendTeamMessage.SendTeamMessage(siteA.SiteId, siteA.OperatorId, "site A's own secret"),
            CancellationToken.None);
        Assert.True(sent.IsSuccess);

        var historyHandler = new GetTeamMessageHistoryHandler(new TeamMessageReadStore(fixture.DataSource));

        // The attack this test exists to catch: site B's own claim, reading its own room, must never
        // come back with site A's message. Before a `site_id` filter this would return one row.
        var attackerView = await historyHandler.HandleAsync(
            new Application.UseCases.GetTeamMessageHistory.GetTeamMessageHistory(siteB.SiteId, null, 50), CancellationToken.None);
        Assert.Empty(attackerView.Messages);

        // The refusal above is about isolation, not about the message being unreadable at all - its
        // own tenant reads it back exactly as sent.
        var ownerView = await historyHandler.HandleAsync(
            new Application.UseCases.GetTeamMessageHistory.GetTeamMessageHistory(siteA.SiteId, null, 50), CancellationToken.None);
        var item = Assert.Single(ownerView.Messages);
        Assert.Equal("site A's own secret", item.Body);
    }

    [Fact]
    public async Task AnOperatorOfOneSite_NeverSeesAnotherSitesTeamMessagesInTheReconnectDelta()
    {
        var siteA = await SeedTenantAsync();
        var siteB = await SeedTenantAsync();

        await using var db = fixture.CreateDbContext();
        var sendHandler = new SendTeamMessageHandler(
            new TeamChatRepository(db, fixture.DataSource, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator()),
            new PermissionChecker(db), new SystemClock(), new UuidV7Generator());

        await sendHandler.HandleAsync(
            new Application.UseCases.SendTeamMessage.SendTeamMessage(siteA.SiteId, siteA.OperatorId, "only for site A"),
            CancellationToken.None);

        var historyHandler = new GetTeamMessageHistoryHandler(new TeamMessageReadStore(fixture.DataSource));

        // Site B's own reconnect catch-up, from the very beginning of its (empty) room's history -
        // must never surface site A's own sequence-1 message just because both rooms happen to use
        // the identical sequence numbering independently.
        var delta = await historyHandler.HandleDeltaAsync(
            new Application.UseCases.GetTeamMessageHistory.GetTeamMessageDelta(siteB.SiteId, 0), CancellationToken.None);
        Assert.Empty(delta);
    }

    /// <summary>One tenant, one operator holding every permission there is on its own site - enough to
    /// prove both the ordinary-operator and the admin-labelled send path in isolation from any other
    /// tenant's data.</summary>
    private async Task<Tenant> SeedTenantAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        await db.SaveChangesAsync();

        return new Tenant(siteId, operatorId);
    }
}
