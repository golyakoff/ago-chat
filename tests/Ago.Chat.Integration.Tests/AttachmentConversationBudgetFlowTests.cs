using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-75`'s own "this is the test that matters": not the store's compare-and-set in isolation
/// (<c>ConversationAttachmentBudgetStoreTests</c>), but the whole path a real presign request takes -
/// <c>CreateAttachmentHandler.HandleAsVisitorAsync</c>/<c>HandleAsOperatorAsync</c>, real Postgres, real
/// presigning against real MinIO (<see cref="AttachmentFixture"/>) - proving ten simultaneous callers
/// cannot together reserve more than the conversation's own budget.
/// </summary>
[Collection(AttachmentCollection.Name)]
public sealed class AttachmentConversationBudgetFlowTests(AttachmentFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsVisitorAsync_TenSimultaneousPresignRequests_NeverTogetherExceedTheConversationBudget()
    {
        var (_, visitorId, _, conversationId) = await SeedAssignedConversationAsync();
        // `23-81` dropped the default per-file ceiling to 5 MiB; this test is about the
        // *conversation* budget, not the per-file one, so it raises `MaxSizeBytes` explicitly
        // rather than shrinking `declaredBytes` and losing "room for exactly three of ten".
        var options = new AttachmentOptions { MaxConversationBytes = 30 * 1024 * 1024, MaxSizeBytes = 10 * 1024 * 1024 };
        const long declaredBytes = 10 * 1024 * 1024; // 10 MiB - room for exactly three of ten.
        const int attempts = 10;

        var results = await Task.WhenAll(Enumerable.Range(0, attempts)
            .Select(async _ =>
            {
                await using var db = fixture.CreateDbContext();
                return await CreateHandler(db, options).HandleAsVisitorAsync(
                    new CreateAttachmentAsVisitor(conversationId, visitorId, "image/png", declaredBytes),
                    CancellationToken.None);
            }));

        Assert.Equal(3, results.Count(r => r.IsSuccess));
        Assert.Equal(attempts - 3, results.Count(r => r.IsFailure));
        Assert.All(results.Where(r => r.IsFailure), r => Assert.Equal("Attachment.ConversationBudgetExceeded", r.Error!.Value.Code));

        // Every successfully presigned attachment actually got a `pending` row - the reservation and
        // the row are never allowed to disagree (this item's own design point: reserved inside the
        // same transaction that creates the row).
        await using var verifyDb = fixture.CreateDbContext();
        var pendingCount = await verifyDb.Attachments.CountAsync(a => a.ConversationId == conversationId);
        Assert.Equal(3, pendingCount);

        Assert.Equal(3 * declaredBytes, await ReadReservedBytesAsync(conversationId));
    }

    /// <summary>`23-75`'s own Scope: "spent by visitor and operator alike" - a mixed batch, five
    /// visitor requests and five operator requests racing the identical conversation budget, proving
    /// the two entry points share one total rather than each getting their own.</summary>
    [Fact]
    public async Task MixedVisitorAndOperatorRequests_ShareOneConversationBudget()
    {
        var (siteId, visitorId, operatorId, conversationId) = await SeedAssignedConversationAsync();
        // Same reasoning as the test above: `MaxSizeBytes` raised explicitly so 10 MiB per file
        // still clears the per-file ceiling and this test keeps exercising the conversation budget.
        var options = new AttachmentOptions { MaxConversationBytes = 30 * 1024 * 1024, MaxSizeBytes = 10 * 1024 * 1024 };
        const long declaredBytes = 10 * 1024 * 1024;

        var visitorCalls = Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var db = fixture.CreateDbContext();
            return await CreateHandler(db, options).HandleAsVisitorAsync(
                new CreateAttachmentAsVisitor(conversationId, visitorId, "image/png", declaredBytes), CancellationToken.None);
        });
        var operatorCalls = Enumerable.Range(0, 5).Select(async _ =>
        {
            await using var db = fixture.CreateDbContext();
            return await CreateHandler(db, options).HandleAsOperatorAsync(
                new CreateAttachmentAsOperator(conversationId, operatorId, siteId, "image/png", declaredBytes),
                CancellationToken.None);
        });

        var results = await Task.WhenAll(visitorCalls.Concat(operatorCalls));

        Assert.Equal(3, results.Count(r => r.IsSuccess));
        Assert.Equal(3 * declaredBytes, await ReadReservedBytesAsync(conversationId));
    }

    private CreateAttachmentHandler CreateHandler(AgoChatDbContext db, AttachmentOptions? options = null) => new(
        new ConversationRepository(db),
        new AttachmentRepository(db),
        fixture.FileStorage,
        new FakeRateLimiter(),
        new PermissionChecker(db),
        new ConversationAttachmentBudgetStore(db),
        new SiteAttachmentStorageBudgetStore(db),
        new SiteRepository(db),
        new BillingSubscriptionRepository(db),
        new EfUnitOfWork(db),
        options ?? new AttachmentOptions(),
        new AttachmentRateLimitOptions(),
        new AttachmentStorageQuotaOptions(),
        new UuidV7Generator(),
        new SystemClock());

    private async Task<(SiteId SiteId, VisitorId VisitorId, OperatorId OperatorId, ConversationId ConversationId)> SeedAssignedConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        // `23-78`: granted at creation - this file's own budget tests exercise both the visitor and
        // the operator upload path against this same conversation, and the visitor path now refuses
        // without a grant before the budget is ever consulted.
        var conversation = Conversation.Start(conversationId, siteId, visitorId, Now, attachmentUploadGrantedByDefault: true);
        conversation.AssignTo(operatorId, Now);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (siteId, visitorId, operatorId, conversationId);
    }

    private async Task<long> ReadReservedBytesAsync(ConversationId conversationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT attachment_bytes_reserved FROM conversations WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", conversationId.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
