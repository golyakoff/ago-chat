using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-74`'s Done-when: "history written while paid survives a later downgrade, proven -
/// `adr/0031` says it does, and nothing asserts it." `MessageRetentionArchiveEndToEndTests`'s own class
/// remarks already referred to this file before it existed - `adr/0031`'s Decision 2 was implemented in
/// `13-06` and exercised indirectly ever since (every message write resolves
/// <see cref="RetentionClass.FromTier"/> once, at write time), but nothing asserted the actual claim: that
/// a later change to <see cref="Site.Tier"/> never reaches back and rewrites an already-written message's
/// own <see cref="Message.RetentionClass"/>.
///
/// <para><b>Deliberately domain-level, not routed through the prune job.</b>
/// <see cref="MessagePartitionPruneJobTests.PruneAsync_LeavesAMessageWrittenUnderAPaidClass_AfterTheOwningSiteLaterDowngradesToFree"/>
/// proves the *consequence* (a downgraded site's older paid-tier messages are not swept by age); this
/// file proves the *mechanism* the consequence depends on - the stored column itself never changes -
/// through the real write path (<see cref="Conversation.AddVisitorMessage"/>,
/// <see cref="Site.ActivateSubscription"/>) rather than a raw SQL update standing in for a tier
/// change.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetentionClassImmutabilityTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset WrittenAt = new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DowngradedAt = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The core proof. A site on `starter` writes a message - `RetentionClass.FromTier(site.Tier)`
    /// stamps `starter` onto it, exactly as <c>MessageBatchWriter</c>'s real write path resolves it. The
    /// site is then downgraded to `free` (<see cref="Site.ActivateSubscription"/>, the same domain method
    /// a real subscription-cancellation webhook drives) and saved. Reloading the message - not the site -
    /// and reading its own <see cref="Message.RetentionClass"/> back is the assertion: `adr/0031`'s
    /// Decision 2 promises this stays `starter`, never silently becoming `free` because the row's owning
    /// site now is.</summary>
    [Fact]
    public async Task ADowngrade_NeverRewritesTheRetentionClassOfAMessageWrittenBeforeIt()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var messageId = new MessageId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            var site = new Site(siteId, $"site_{siteId.Value:N}", []);
            site.ActivateSubscription(SubscriptionTierBands.Starter, seatLimit: 3, extraAdministrators: 0, WrittenAt);
            db.Sites.Add(site);
            db.Visitors.Add(new Visitor(visitorId, siteId, WrittenAt));
            db.SaveChanges();

            var conversation = Conversation.Start(conversationId, siteId, visitorId, WrittenAt);
            conversation.AddVisitorMessage(
                visitorId, messageId, new MessageBody("written while starter"), WrittenAt,
                retentionClass: RetentionClass.FromTier(site.Tier));
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        try
        {
            await using (var db = fixture.CreateDbContext())
            {
                var site = await db.Sites.FirstOrDefaultAsync(s => s.Id == siteId);
                site!.ActivateSubscription("free", seatLimit: 2, extraAdministrators: 0, DowngradedAt);
                await db.SaveChangesAsync();
            }

            await using (var db = fixture.CreateDbContext())
            {
                var reloadedSite = await db.Sites.FirstOrDefaultAsync(s => s.Id == siteId);
                Assert.Equal("free", reloadedSite!.Tier);

                var reloadedConversation = await db.Conversations
                    .Include("_messages")
                    .FirstAsync(c => c.Id == conversationId);
                var message = reloadedConversation.Messages.Single(m => m.Id == messageId);
                Assert.Equal(SubscriptionTierBands.Starter, message.RetentionClass.Value);
            }
        }
        finally
        {
            await using var cleanup = fixture.CreateDbContext();
            var conversation = await cleanup.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
            if (conversation is not null)
            {
                cleanup.Conversations.Remove(conversation);
            }

            var visitor = await cleanup.Visitors.FirstOrDefaultAsync(v => v.Id == visitorId);
            if (visitor is not null)
            {
                cleanup.Visitors.Remove(visitor);
            }

            var site = await cleanup.Sites.FirstOrDefaultAsync(s => s.Id == siteId);
            if (site is not null)
            {
                cleanup.Sites.Remove(site);
            }

            await cleanup.SaveChangesAsync();
        }
    }
}
