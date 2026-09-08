using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-59`/`adr/0147`: "chat publishes, through the outbox, in the same transaction as the contact
/// write" - proven here against real Postgres, the same shape <c>RoleAssignmentsChangedOutboxTests</c>
/// already proves for its own event. The consumer half - AGO Calendar creating a customer - lives in
/// `ago-calendar`, a different repository with a different database; this file cannot reach it and does
/// not try to (`adr/0093`).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContactCollectedOutboxTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>`ago_chat`'s own half of Done-when #5 ("neither product queries the other's schema,
    /// asserted by a test") - the mirror of `ago-calendar`'s own <c>RoleAssignmentProjectionDemonstrationTests</c>
    /// check, applied to the table this item's own item is about: chat's schema holds no `customers`
    /// table at all, not merely an empty one - there is nothing here for a cross-product read to reach
    /// even if a future change tried.</summary>
    [Fact]
    public async Task ChatsOwnSchema_HoldsNoCustomersTable()
    {
        await using var db = fixture.CreateDbContext();
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass('public.customers') IS NOT NULL";
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        Assert.False((bool)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task AnOperatorRecordingAContactDetail_StagesOneContactCollectedRow_InTheSameTransactionAsTheWrite()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        var roleId = Guid.NewGuid();
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", ["https://example.test"], "Test Site", Now));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, "kc-subject"));
            seed.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationSend.Value],
            });
            seed.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            seed.Conversations.Add(conversation);
            await seed.SaveChangesAsync();
        }

        Guid detailId;
        await using (var db = fixture.CreateDbContext())
        {
            var handler = new RecordVisitorContactDetailHandler(
                new ConversationRepository(db), new VisitorContactDetailRepository(db), new SiteRepository(db),
                new AcceptanceRepository(db), new PermissionChecker(db), new AlwaysAllowRateLimiter(),
                new ContactDetailRateLimitOptions(), new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(),
                new FixedClock(Now));

            var result = await handler.HandleAsOperatorAsync(
                new RecordVisitorContactDetailAsOperator(operatorId, siteId, conversationId, "Phone", "+1 555 0100"),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            detailId = result.Value.Id;
        }

        await using var verify = fixture.CreateDbContext();
        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(ContactCollected) && o.PartitionKey == detailId.ToString(), CancellationToken.None);

        var contract = JsonSerializer.Deserialize<ContactCollected>(outboxRow.Payload)!;
        Assert.Equal(detailId, contract.ContactDetailId);
        Assert.Equal(siteId.Value, contract.SiteId);
        Assert.Equal("Phone", contract.Kind);
        Assert.Equal("+1 555 0100", contract.Value);
        Assert.Null(outboxRow.PublishedAt);

        var savedDetail = await verify.VisitorContactDetails.SingleAsync(d => d.Id == new VisitorContactDetailId(detailId), CancellationToken.None);
        Assert.Equal(visitorId, savedDetail.VisitorId);
    }

    [Fact]
    public async Task AVisitorRecordingTheirOwnContactDetail_StagesOneContactCollectedRow_Too()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", ["https://example.test"], "Test Site", Now));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            seed.Conversations.Add(conversation);
            await seed.SaveChangesAsync();
        }

        Guid detailId;
        await using (var db = fixture.CreateDbContext())
        {
            var handler = new RecordVisitorContactDetailHandler(
                new ConversationRepository(db), new VisitorContactDetailRepository(db), new SiteRepository(db),
                new AcceptanceRepository(db), new PermissionChecker(db), new AlwaysAllowRateLimiter(),
                new ContactDetailRateLimitOptions(), new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(),
                new FixedClock(Now));

            var result = await handler.HandleAsVisitorAsync(
                new RecordVisitorContactDetailAsVisitor(conversationId, visitorId, "Phone", "+1 555 0177"),
                CancellationToken.None);

            Assert.True(result.IsSuccess);
            detailId = result.Value.Id;
        }

        await using var verify = fixture.CreateDbContext();
        var exists = await verify.Set<OutboxMessage>().AnyAsync(
            o => o.Type == nameof(ContactCollected) && o.PartitionKey == detailId.ToString(), CancellationToken.None);
        Assert.True(exists);
    }

    private sealed class AlwaysAllowRateLimiter : IRateLimiter
    {
        public Task<RateLimitDecision> CheckAsync(
            RateLimitKey key, RateLimitRule rule, CancellationToken cancellationToken) =>
            Task.FromResult(new RateLimitDecision(Allowed: true, RetryAfter: TimeSpan.Zero));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
