using System.Diagnostics;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Pipeline;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `4-05`: `MessageBatchWriter` in isolation, real Postgres - the participant/state/not-found checks
/// `SendVisitorMessageHandlerTests`/`SendOperatorMessageHandlerTests` used to cover moved here along
/// with the write itself (those handler-level tests now only cover pre-enqueue checks, using a fake
/// pipeline - see their own remarks). `MessageBatchWriter.FlushAsync` stays internal to this
/// project; reached directly via this project's `InternalsVisibleTo`.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessageBatchWriterTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FlushAsync_VisitorMessageForAConversationThatIsNotTheirs_FailsThatAckWithForbidden()
    {
        var (_, conversationId) = await SeedWaitingConversationAsync();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, someoneElse.Value, "hello");

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_OperatorNotAssignedToTheConversation_FailsThatAckWithForbidden()
    {
        var (siteId, _, conversationId) = await SeedAssignedConversationAsync();
        var someoneElse = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Operators.Add(new Operator(someoneElse, siteId, OperatorStatus.Online, capacity: 5));
            await db.SaveChangesAsync();
        }

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Operator, someoneElse.Value, "hi");

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_OperatorMessage_WhenNoOperatorIsAssignedYet_FailsThatAckWithInvalidState()
    {
        var (_, conversationId) = await SeedWaitingConversationAsync();
        var operatorId = new OperatorId(Guid.NewGuid());

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Operator, operatorId.Value, "hi");

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_VisitorMessage_WhenTheConversationIsClosed_FailsThatAckWithInvalidState()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var conversation = await db.Conversations.FirstAsync(c => c.Id == conversationId);
            conversation.Close(Now);
            await db.SaveChangesAsync();
        }

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "hello");

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_WhenTheConversationDoesNotExist_FailsThatAckWithNotFound()
    {
        var missingConversationId = new ConversationId(Guid.NewGuid());

        var result = await FlushOneAsync(missingConversationId, MessageAuthorKind.Visitor, Guid.NewGuid(), "hello");

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>
    /// `13-06`/`adr/0031`'s own Done-when: "retention_class is stamped at write time and provably
    /// never updated afterwards - including a test that changing a site's tier leaves existing rows
    /// untouched." Drives the real production write path end to end (`MessageBatchWriter` ->
    /// `GetSiteConfigByIdHandler` -> `RetentionClass.FromTier`) against a real Postgres, through the
    /// same <see cref="NoOpCache"/> every other test in this file uses - a cache that never actually
    /// caches means every send re-reads the site's *current* tier fresh, which is exactly what proves
    /// the immutability is a property of `Message`/the write path, not an accident of a stale cache
    /// entry masking a tier change that would otherwise have leaked backwards.
    /// </summary>
    [Fact]
    public async Task FlushAsync_StampsRetentionClassFromTheSitesCurrentTier_AndNeverUpdatesAnAlreadyWrittenMessage()
    {
        var (siteId, visitorId, conversationId) = await SeedWaitingConversationWithSiteIdAsync();

        var firstResult = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "written under free");
        Assert.True(firstResult.IsSuccess);

        // The tier change - the exact event adr/0031's Decision 2 says must move no existing row.
        await using (var db = fixture.CreateDbContext())
        {
            var site = await db.Sites.SingleAsync(s => s.Id == siteId);
            site.ActivateSubscription(SubscriptionTierBands.Starter, seatLimit: 10, extraAdministrators: 0, Now);
            await db.SaveChangesAsync();
        }

        var secondResult = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "written under starter");
        Assert.True(secondResult.IsSuccess);

        await using var verify = fixture.CreateDbContext();
        var firstMessage = await verify.Set<Message>().SingleAsync(m => m.ConversationId == conversationId && m.Sequence == firstResult.Value);
        var secondMessage = await verify.Set<Message>().SingleAsync(m => m.ConversationId == conversationId && m.Sequence == secondResult.Value);

        // The load-bearing assertion: the first message's class is still "free", stamped once at
        // write time and never re-derived from the site's now-different tier.
        Assert.Equal(RetentionClass.Free.Value, firstMessage.RetentionClass.Value);
        Assert.Equal(SubscriptionTierBands.Starter, secondMessage.RetentionClass.Value);
    }

    [Fact]
    public async Task FlushAsync_MultipleMessagesForTheSameConversationInOneBatch_AppliesThemInOrder_GapFreeSequence()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var writer = CreateWriter();

        var items = Enumerable.Range(1, 5)
            .Select(i => new InboundMessage(
                new PendingMessage(conversationId, MessageAuthorKind.Visitor, visitorId, new MessageBody($"message {i}")),
                new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously),
                Stopwatch.GetTimestamp()))
            .ToList();

        await writer.FlushAsync(items, CancellationToken.None);

        var sequences = new List<int>();
        foreach (var item in items)
        {
            var result = await item.Ack.Task;
            Assert.True(result.IsSuccess);
            sequences.Add(result.Value);
        }

        Assert.Equal([1, 2, 3, 4, 5], sequences);
    }

    [Fact]
    public async Task FlushAsync_OneMessagesDomainFailure_DoesNotPreventOthersInTheSameBatchFromSucceeding()
    {
        var (goodVisitorId, goodConversationId) = await SeedWaitingConversationAsync();
        var (closedVisitorId, closedConversationId) = await SeedWaitingConversationAsync();
        await using (var db = fixture.CreateDbContext())
        {
            var closed = await db.Conversations.FirstAsync(c => c.Id == closedConversationId);
            closed.Close(Now);
            await db.SaveChangesAsync();
        }

        var writer = CreateWriter();
        var goodAck = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var badAck = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var items = new List<InboundMessage>
        {
            new(new PendingMessage(goodConversationId, MessageAuthorKind.Visitor, goodVisitorId, new MessageBody("hello")), goodAck, Stopwatch.GetTimestamp()),
            new(new PendingMessage(closedConversationId, MessageAuthorKind.Visitor, closedVisitorId, new MessageBody("too late")), badAck, Stopwatch.GetTimestamp()),
        };

        await writer.FlushAsync(items, CancellationToken.None);

        var goodResult = await goodAck.Task;
        var badResult = await badAck.Task;
        Assert.True(goodResult.IsSuccess);
        Assert.Equal(1, goodResult.Value);
        Assert.True(badResult.IsFailure);
        Assert.Equal("Conversation.InvalidState", badResult.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_MessageReferencingAReadyAttachment_Succeeds_AndLinksTheAttachment()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var attachmentId = await SeedAttachmentAsync(conversationId, AttachmentState.Ready);

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "look at this", attachmentId);

        Assert.True(result.IsSuccess);
        await using var verify = fixture.CreateDbContext();
        var message = await verify.Set<Message>().SingleAsync(m => m.ConversationId == conversationId);
        Assert.Equal(attachmentId, message.AttachmentId);
        var attachment = await verify.Attachments.SingleAsync(a => a.Id == attachmentId);
        Assert.Equal(message.Id, attachment.MessageId);
    }

    [Fact]
    public async Task FlushAsync_MessageReferencingAPendingAttachment_FailsThatAckWithAttachmentNotReady()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var attachmentId = await SeedAttachmentAsync(conversationId, AttachmentState.Pending);

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "look at this", attachmentId);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.NotReady", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_MessageReferencingADeletedAttachment_FailsThatAckWithAttachmentNotReady()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var attachmentId = await SeedAttachmentAsync(conversationId, AttachmentState.Deleted);

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "look at this", attachmentId);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.NotReady", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_MessageReferencingAnAttachmentFromAnotherConversation_FailsThatAckWithForbidden()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var (_, otherConversationId) = await SeedWaitingConversationAsync();
        var attachmentId = await SeedAttachmentAsync(otherConversationId, AttachmentState.Ready);

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "look at this", attachmentId);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_MessageReferencingAnUnknownAttachment_FailsThatAckWithAttachmentNotFound()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();
        var missingAttachmentId = new AttachmentId(Guid.NewGuid());

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "look at this", missingAttachmentId);

        Assert.True(result.IsFailure);
        Assert.Equal("Attachment.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task FlushAsync_OnSuccess_WritesTheOutboxRowInTheSameTransactionAsTheMessage()
    {
        var (visitorId, conversationId) = await SeedWaitingConversationAsync();

        var result = await FlushOneAsync(conversationId, MessageAuthorKind.Visitor, visitorId, "hello");
        Assert.True(result.IsSuccess);

        await using var verify = fixture.CreateDbContext();
        var message = await verify.Set<Message>().SingleAsync(m => m.ConversationId == conversationId);
        var outboxRow = await verify.Set<Ago.Platform.Persistence.Postgres.OutboxMessage>().SingleAsync(o => o.Id == message.Id.Value);
        Assert.Equal("MessageAccepted", outboxRow.Type);
        Assert.Null(outboxRow.PublishedAt);
    }

    /// <summary>
    /// `25-109`'s own regression test - the root-cause race, reproduced deterministically against real
    /// Postgres with no new production-code test seam. `FlushBatchAsync` already takes `IClock` and
    /// calls `clock.UtcNow` once per message, after that message's own conversation is loaded and
    /// before `SaveChangesAsync` - <see cref="RacingClock"/>'s first call rides that existing seam to
    /// run an independent `UPDATE` against the *first* conversation this flush loads, on a second
    /// connection that commits immediately, simulating exactly what `Ago.Chat.Worker`'s
    /// `UnreadCounterConsumer` used to do concurrently under load (before `25-109`'s other change moved
    /// it off the aggregate) - some other cross-process writer of `conversations` (`ConversationAssignmentJob`
    /// and friends, per this item's own Scope) still can, which is exactly what this test stands in for.
    ///
    /// <para><b>Fails-before</b> (verified by temporarily reverting <c>MessageBatchWriter</c>'s retry
    /// loop to the pre-`25-109` single `try`/catch-and-fail-everything shape): every ack in this
    /// four-message, three-conversation batch returns <c>Conversation.Unavailable</c> - the one raced
    /// row's lost optimistic-concurrency check fails the whole `SaveChangesAsync`, and the old catch-all
    /// fails every pending ack in the batch, including the two conversations that were never touched by
    /// the race at all.</para>
    ///
    /// <para><b>After</b>: the race only ever fires once (<see cref="RacingClock"/>'s own guard), so the
    /// first attempt is the only one that can lose it - the retry loop's second attempt reloads every
    /// conversation fresh, finds nothing contending any more, and commits cleanly. All four acks
    /// succeed, and the raced conversation's own two messages land as sequence 1 then 2 - gap-free
    /// ascending even though the aggregate that produced them was loaded and reloaded across two
    /// separate attempts.</para>
    /// </summary>
    [Fact]
    public async Task FlushAsync_AConcurrentWriterBumpsAConversationsXminMidFlush_RetriesAndEveryMessageEventuallySucceeds()
    {
        var (racedVisitorId, racedConversationId) = await SeedWaitingConversationAsync();
        var (otherVisitorId1, otherConversationId1) = await SeedWaitingConversationAsync();
        var (otherVisitorId2, otherConversationId2) = await SeedWaitingConversationAsync();

        var acks = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToList();
        // The raced conversation's two messages come first in the list - GroupBy preserves
        // first-encounter order for its groups, so this is what makes it the group RacingClock's own
        // first UtcNow call fires during, matching "the first conversation this flush loads" above.
        var items = new List<InboundMessage>
        {
            new(new PendingMessage(racedConversationId, MessageAuthorKind.Visitor, racedVisitorId, new MessageBody("first")), acks[0], Stopwatch.GetTimestamp()),
            new(new PendingMessage(racedConversationId, MessageAuthorKind.Visitor, racedVisitorId, new MessageBody("second")), acks[1], Stopwatch.GetTimestamp()),
            new(new PendingMessage(otherConversationId1, MessageAuthorKind.Visitor, otherVisitorId1, new MessageBody("unrelated 1")), acks[2], Stopwatch.GetTimestamp()),
            new(new PendingMessage(otherConversationId2, MessageAuthorKind.Visitor, otherVisitorId2, new MessageBody("unrelated 2")), acks[3], Stopwatch.GetTimestamp()),
        };

        var writer = new MessageBatchWriter(
            fixture.DataSource, new RacingClock(fixture, racedConversationId.Value), new UuidV7Generator(), new NoOpCache(),
            Options.Create(new SiteActivityWatchdogOptions()), NullLogger<MessageBatchWriter>.Instance);

        await writer.FlushAsync(items, CancellationToken.None);

        var results = new List<Result<int>>();
        foreach (var ack in acks)
        {
            results.Add(await ack.Task);
        }

        Assert.All(results, r => Assert.True(r.IsSuccess, r.IsFailure ? r.Error!.Value.Code : null));
        Assert.Equal(1, results[0].Value);
        Assert.Equal(2, results[1].Value);
        Assert.Equal(1, results[2].Value);
        Assert.Equal(1, results[3].Value);
    }

    /// <summary>See <see cref="FlushAsync_AConcurrentWriterBumpsAConversationsXminMidFlush_RetriesAndEveryMessageEventuallySucceeds"/>'s
    /// own remarks. Fires the race exactly once, on its first call ever - a real concurrent writer
    /// (`UnreadCounterConsumer`, or the item's own secondary candidate) does not keep re-touching the
    /// same row forever either, so a single, one-shot conflict is the faithful shape to reproduce, not
    /// a permanently-contended row.</summary>
    private sealed class RacingClock(PostgresFixture fixture, Guid conversationIdToRace) : IClock
    {
        private bool _hasRaced;

        public DateTimeOffset UtcNow
        {
            get
            {
                if (!_hasRaced)
                {
                    _hasRaced = true;
                    RaceAsync().GetAwaiter().GetResult();
                }

                return DateTimeOffset.UtcNow;
            }
        }

        private async Task RaceAsync()
        {
            // A second, independent connection - not fixture.CreateDbContext()'s own transaction, and
            // not MessageBatchWriter's: this simulates a genuinely separate process/connection
            // committing its own UPDATE to the same row while MessageBatchWriter's own flush is still
            // mid-load, exactly `25-109`'s own root-cause window.
            await using var db = fixture.CreateDbContext();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE conversations SET operator_unread_count = operator_unread_count + 1 WHERE id = {conversationIdToRace}");
        }
    }

    private MessageBatchWriter CreateWriter() =>
        new(fixture.DataSource, new SystemClock(), new UuidV7Generator(), new NoOpCache(), Options.Create(new SiteActivityWatchdogOptions()), NullLogger<MessageBatchWriter>.Instance);

    private async Task<Result<int>> FlushOneAsync(
        ConversationId conversationId, MessageAuthorKind authorKind, Guid authorId, string body, AttachmentId? attachmentId = null)
    {
        var writer = CreateWriter();
        var ack = new TaskCompletionSource<Result<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new InboundMessage(
            new PendingMessage(conversationId, authorKind, authorId, new MessageBody(body), attachmentId), ack, Stopwatch.GetTimestamp());
        await writer.FlushAsync([item], CancellationToken.None);
        return await ack.Task;
    }

    private async Task<AttachmentId> SeedAttachmentAsync(ConversationId conversationId, AttachmentState state)
    {
        await using var db = fixture.CreateDbContext();
        var conversation = await db.Conversations.FirstAsync(c => c.Id == conversationId);
        var attachment = Attachment.CreatePending(
            new AttachmentId(Guid.NewGuid()), conversation.SiteId, conversationId, "site/x/conv/y/z.png", "image/png", 42, Now);
        if (state != AttachmentState.Pending)
        {
            attachment.ConfirmReady(42, "image/png", Now);
        }

        if (state == AttachmentState.Deleted)
        {
            attachment.MarkDeleted();
        }

        db.Attachments.Add(attachment);
        await db.SaveChangesAsync();
        return attachment.Id;
    }

    private async Task<(Guid VisitorId, ConversationId ConversationId)> SeedWaitingConversationAsync()
    {
        var (_, visitorId, conversationId) = await SeedWaitingConversationWithSiteIdAsync();
        return (visitorId, conversationId);
    }

    private async Task<(SiteId SiteId, Guid VisitorId, ConversationId ConversationId)> SeedWaitingConversationWithSiteIdAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
        await db.SaveChangesAsync();

        return (siteId, visitorId.Value, conversationId);
    }

    private async Task<(SiteId SiteId, Guid VisitorId, ConversationId ConversationId)> SeedAssignedConversationAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
        // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
        // visitor's own real first message before AssignTo, which still only accepts Waiting.
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        conversation.AssignTo(operatorId, Now);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (siteId, visitorId.Value, conversationId);
    }
}
