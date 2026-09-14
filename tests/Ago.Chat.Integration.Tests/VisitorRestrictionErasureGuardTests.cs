using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-78`'s own scoping decision, guarded rather than merely documented: a visitor's restriction
/// history (`23-69`'s spam mutes, `23-77`'s manual blocks) is the visitor's <i>current standing on the
/// site</i>, not evidence about one conversation - so <see cref="ConversationErasureJob"/> must never
/// remove a <c>visitor_restrictions</c> row, whether or not its own <c>source_conversation_id</c> names
/// the conversation being erased. See <c>SiteErasureQuery.DeleteVisitorRestrictionsForSiteAsync</c>'s
/// own remarks for the full argument: deleting a restriction as a side effect of erasing the one
/// conversation that happened to trigger it would silently lift an active spam mute or block, a
/// moderation regression dressed as privacy compliance. A restriction is drained explicitly only at
/// site scope (<see cref="SiteErasureIntegrationTests"/>'s own new coverage), never here.
///
/// <para><b>This is a guard, not (only) a fix.</b> Before `25-78`, nothing in
/// <see cref="ConversationErasureJob"/> ever mentioned <c>visitor_restrictions</c> at all - the row
/// already survived a conversation's own erasure by omission, not by design. This test exists so a
/// later change - a foreign key added "for consistency" with `conversation_notes`, or an explicit drain
/// step added here by a reviewer reasoning from <see cref="Ago.Chat.Worker.ConversationErasureQuery.DeleteContactDetailsForVisitorAsync"/>'s
/// own visitor-wide shape - cannot quietly reverse this item's own scoping decision with no suite going
/// red. The identical shape <c>AcceptanceRecordErasureGuardTests</c>/<c>ConversationAssignmentErasureGuardTests</c>
/// already establish for their own erasure exceptions.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class VisitorRestrictionErasureGuardTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EraseConversationAsync_DeletesTheConversation_ButLeavesARestrictionItSourcedStanding()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var restrictionId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await db.SaveChangesAsync();
        }

        // A Spam mute *sourced from the very conversation about to be erased* - the one row this test
        // exists to prove survives that conversation's own deletion. Its whole point is to keep gating
        // this visitor's future conversations on this site, which erasing one past conversation must
        // not silently switch off.
        var restrictions = new VisitorRestrictionRepository(fixture.DataSource);
        await restrictions.RestrictAsync(
            siteId, visitorId, operatorId, VisitorRestrictionKind.Spam, Now.AddHours(24), conversationId,
            restrictionId, Now, CancellationToken.None);

        var erasureOptions = new ConversationErasureJobOptions();
        var archiveEraser = new ConversationArchiveEraser(
            new FakeFileStorage(), new MessageArchiveRepository(fixture.DataSource), erasureOptions,
            NullLogger<ConversationArchiveEraser>.Instance);
        var job = new ConversationErasureJob(
            fixture.DataSource, new FakeFileStorage(), archiveEraser, new SystemClock(),
            Options.Create(erasureOptions), NullLogger<ConversationErasureJob>.Instance);

        var erased = await job.EraseConversationAsync(conversationId.Value, siteId.Value, visitorId.Value, null, CancellationToken.None);
        Assert.True(erased);

        await using var verify = fixture.DataSource.CreateConnection();
        await verify.OpenAsync();
        Assert.Equal(0, await CountAsync(verify, "select count(*) from conversations where id = @id", conversationId.Value));

        // The conversation is gone; the restriction it sourced is not, and is still active - not merely
        // present as an orphaned row nothing reads any more.
        await using var restrictionCommand = new NpgsqlCommand(
            "select kind, site_id, visitor_id, lifted_at from visitor_restrictions where id = @id", verify);
        restrictionCommand.Parameters.AddWithValue("id", restrictionId);
        await using var reader = await restrictionCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Spam", reader.GetString(0));
        Assert.Equal(siteId.Value, reader.GetGuid(1));
        Assert.Equal(visitorId.Value, reader.GetGuid(2));
        Assert.True(reader.IsDBNull(3));

        await using var isActiveConnection = fixture.DataSource.CreateConnection();
        await isActiveConnection.OpenAsync();
        Assert.True(await restrictions.IsActiveAsync(siteId, visitorId, Now.AddHours(1), CancellationToken.None));
    }

    [Fact]
    public async Task EraseConversationAsync_DeletesTheConversation_ButLeavesAnUnrelatedRestrictionStandingToo()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var erasedConversationId = new ConversationId(Guid.NewGuid());
        var otherConversationId = new ConversationId(Guid.NewGuid());
        var restrictionId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            db.Conversations.Add(Conversation.Start(erasedConversationId, siteId, visitorId, Now));
            await db.SaveChangesAsync();
        }

        // An indefinite Block, sourced from a *different* conversation than the one being erased - the
        // "wide" half of this item's own scoping question. Widening ConversationErasureJob's drain to
        // every restriction the visitor has (the same reach DeleteContactDetailsForVisitorAsync gives
        // visitor_contact_details) would delete this row too; this test proves it does not.
        var restrictions = new VisitorRestrictionRepository(fixture.DataSource);
        await restrictions.RestrictAsync(
            siteId, visitorId, operatorId, VisitorRestrictionKind.Block, null, otherConversationId,
            restrictionId, Now, CancellationToken.None);

        var erasureOptions = new ConversationErasureJobOptions();
        var archiveEraser = new ConversationArchiveEraser(
            new FakeFileStorage(), new MessageArchiveRepository(fixture.DataSource), erasureOptions,
            NullLogger<ConversationArchiveEraser>.Instance);
        var job = new ConversationErasureJob(
            fixture.DataSource, new FakeFileStorage(), archiveEraser, new SystemClock(),
            Options.Create(erasureOptions), NullLogger<ConversationErasureJob>.Instance);

        var erased = await job.EraseConversationAsync(
            erasedConversationId.Value, siteId.Value, visitorId.Value, null, CancellationToken.None);
        Assert.True(erased);

        await using var verify = fixture.DataSource.CreateConnection();
        await verify.OpenAsync();
        Assert.Equal(
            0, await CountAsync(verify, "select count(*) from conversations where id = @id", erasedConversationId.Value));
        Assert.Equal(
            1, await CountAsync(verify, "select count(*) from visitor_restrictions where id = @id", restrictionId));
        Assert.True(await restrictions.IsActiveAsync(siteId, visitorId, Now.AddDays(365), CancellationToken.None));
    }

    private static async Task<int> CountAsync(NpgsqlConnection connection, string sql, Guid id)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }
}
