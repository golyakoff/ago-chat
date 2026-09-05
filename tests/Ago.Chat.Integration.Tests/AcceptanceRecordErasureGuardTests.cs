using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `docs/adr/0111-*`'s own central claim, guarded rather than merely documented: an acceptance record
/// is evidence of a lawful basis at the time of processing, and this project's own erasure jobs must
/// not remove it - for any of the three subjects the record can name (`24-01`'s own Done-when: "the
/// case the decision does *not* remove, so a later change cannot quietly reverse it").
///
/// <para><b>This is a guard, not a fix - true today by construction.</b>
/// <c>acceptance_records.subject_id</c> carries no foreign key at all
/// (<c>AcceptanceRecordConfiguration</c>'s own remarks), so there is no cascade for either erasure job
/// to trigger even if it wanted to, and neither job's own code mentions `acceptance_records` - there is
/// no code path from erasure to this table to break. The identical shape
/// <c>ConversationAssignmentErasureGuardTests</c> already established for `conversation_assignments`
/// and a different amendment (`adr/0101`); this is that same guard, for this table's own erasure
/// exception.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AcceptanceRecordErasureGuardTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EraseConversationAsync_DeletesTheConversation_ButLeavesTheVisitorsAcceptanceRecordStanding()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var acceptanceId = new AcceptanceRecordId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));

            // The visitor's own acceptance - standing in for whatever `24-05` eventually records at
            // the contact form. This is the row this test exists to prove survives the conversation's
            // (and the visitor's own contact details') erasure.
            await new AcceptanceRepository(db).SaveAsync(
                AcceptanceRecord.ForVisitor(acceptanceId, visitorId, "processing-notice", "v1", Now),
                CancellationToken.None);

            await db.SaveChangesAsync();
        }

        // `24-09` gave the job its archive eraser; built here the same way
        // `ConversationAssignmentErasureGuardTests` builds it, so the two sibling guards over the same
        // job stay identical rather than drifting into two ways of constructing it.
        var erasureOptions = new ConversationErasureJobOptions();
        var archiveEraser = new ConversationArchiveEraser(
            new FakeFileStorage(), new MessageArchiveRepository(fixture.DataSource), erasureOptions,
            NullLogger<ConversationArchiveEraser>.Instance);
        var job = new ConversationErasureJob(
            fixture.DataSource, new FakeFileStorage(), archiveEraser, new SystemClock(),
            Options.Create(erasureOptions), NullLogger<ConversationErasureJob>.Instance);

        var erased = await job.EraseConversationAsync(conversationId.Value, siteId.Value, visitorId.Value, null, CancellationToken.None);
        Assert.True(erased);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(0, await verify.Conversations.CountAsync(c => c.Id == conversationId));

        // The conversation is gone; the visitor's own acceptance record is not, and is still readable
        // through the ordinary DbSet - not merely present as an orphaned row nothing can reach.
        var surviving = await verify.AcceptanceRecords.AsNoTracking().SingleAsync(a => a.Id == acceptanceId);
        Assert.Equal(AcceptanceSubjectKind.Visitor, surviving.SubjectKind);
        Assert.Equal(visitorId.Value, surviving.SubjectId);
        Assert.Equal("v1", surviving.DocumentVersion);
    }

    [Fact]
    public async Task DeleteSiteAsync_DeletesTheSite_ButLeavesTheTenantsAndOperatorsAcceptanceRecordsStanding()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var tenantAcceptanceId = new AcceptanceRecordId(Guid.NewGuid());
        var operatorAcceptanceId = new AcceptanceRecordId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));

            var acceptances = new AcceptanceRepository(db);
            // The tenant's own acceptance (subject_id = the site's own id - AcceptanceSubjectKind.Tenant's
            // own remarks on why a SiteId is this subject kind's id type) and an operator's own
            // acceptance - both would cascade away if this table carried a foreign key to sites/
            // operators the way conversation_notes does; the point of this test is that it does not.
            await acceptances.SaveAsync(
                AcceptanceRecord.ForTenant(tenantAcceptanceId, siteId, "terms-of-service", "v1", Now), CancellationToken.None);
            await acceptances.SaveAsync(
                AcceptanceRecord.ForOperator(operatorAcceptanceId, operatorId, "operator-basis", "v1", Now), CancellationToken.None);

            await db.SaveChangesAsync();
        }

        // The raw connection SiteErasureJob itself uses, called directly rather than through the full
        // job - SiteErasureJob's own dependencies (a Keycloak identity provisioner, a cache-invalidation
        // publisher) are orchestration this guard does not need: what it tests is the DELETE statement's
        // own cascade behaviour, which SiteErasureQuery.DeleteSiteAsync issues on its own.
        await using (var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None))
        {
            var deleted = await SiteErasureQuery.DeleteSiteAsync(connection, siteId.Value, CancellationToken.None);
            Assert.Equal(1, deleted);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(0, await verify.Sites.CountAsync(s => s.Id == siteId));
        Assert.Equal(0, await verify.Operators.CountAsync(o => o.Id == operatorId));

        var survivingTenant = await verify.AcceptanceRecords.AsNoTracking().SingleAsync(a => a.Id == tenantAcceptanceId);
        Assert.Equal(AcceptanceSubjectKind.Tenant, survivingTenant.SubjectKind);
        Assert.Equal(siteId.Value, survivingTenant.SubjectId);

        var survivingOperator = await verify.AcceptanceRecords.AsNoTracking().SingleAsync(a => a.Id == operatorAcceptanceId);
        Assert.Equal(AcceptanceSubjectKind.Operator, survivingOperator.SubjectKind);
        Assert.Equal(operatorId.Value, survivingOperator.SubjectId);
    }
}
