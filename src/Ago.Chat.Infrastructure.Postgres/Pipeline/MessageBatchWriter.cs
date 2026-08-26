using System.Diagnostics;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres.Pipeline;

/// <summary>
/// `4-05`: the actual Postgres write - one connection, one transaction, one
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> call covering every conversation
/// touched by the whole batch, mirroring <c>Ago.Chat.Worker.OperatorConversationReleaser</c>'s own
/// "build one <c>AgoChatDbContext</c> on the batch's own transaction via
/// <c>Database.UseTransactionAsync</c>" shape (`4-04`) rather than going through
/// <c>IConversationRepository.SaveAsync</c> per message, which would call
/// <c>SaveChangesAsync</c> once per conversation instead of once per batch. `adr/0005`'s
/// outbox-in-the-same-transaction rule applies to the batch as a whole: every row's outbox entry
/// commits with every row, or none of them do.
///
/// Grouped by <see cref="ConversationId"/> so a batch containing several messages for the same
/// conversation (concurrent senders in one conversation, naturally coalesced by
/// <c>ConversationSequencer</c> across flushes but not *within* one) still loads that conversation
/// only once and applies its messages in their original relative order - <c>LINQ</c>'s
/// <c>GroupBy</c> preserves within-group order, which is what keeps the resulting `sequence`
/// gap-free ascending for that conversation even inside a single flush.
///
/// Each message's own domain-invariant failure (participant mismatch, wrong state, conversation not
/// found) fails only that message's own ack, immediately - it does not depend on whether the batch's
/// eventual commit succeeds, since a rejected message was never staged for persistence at all. A
/// batch-wide failure (the commit itself throwing) fails every message that *was* staged, since none
/// of them actually landed.
/// </summary>
public sealed class MessageBatchWriter(
    NpgsqlDataSource dataSource, IClock clock, IIdGenerator idGenerator, ILogger<MessageBatchWriter> logger)
{
    internal async Task FlushAsync(IReadOnlyList<InboundMessage> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        // `7-01`: nfr.md's "DB" stage - one span per flush, covering the whole batch (this is a
        // genuine batch write: several messages, possibly from several different senders' own
        // traces, land in one transaction/one SaveChangesAsync). Real Npgsql instrumentation nests
        // its own per-command spans inside this one for free, since Activity.Current is what it
        // checks. Parenting is honest about the batching-vs-tracing tension rather than pretending
        // it away: the first item's own trace becomes this span's real parent (correct, and the only
        // case nfr.md's Done-when actually tests - a batch of one), every other item in the same
        // flush gets an ActivityLink instead of a false second parent - OTel's own documented shape
        // for "this span was influenced by, but is not a child of, several other traces." Each row's
        // own outbox entry still gets *its own* correct trace context below, independent of this
        // span's parent - see IOutboxWriter.Enqueue's own remarks for why that has to be explicit,
        // not read from this ambient activity.
        ChatTracing.TryParseTraceParent(batch[0].Message.TraceParent, out var batchParent);
        var links = batch.Count > 1
            ? batch.Skip(1)
                .Select(item => ChatTracing.TryParseTraceParent(item.Message.TraceParent, out var context) ? new ActivityLink(context) : (ActivityLink?)null)
                .OfType<ActivityLink>()
                .ToList()
            : [];
        using var activity = ChatTracing.Source.StartActivity(
            ChatTracing.SpanNames.PipelinePersistBatch, ActivityKind.Internal, batchParent, links: links);
        activity?.SetTag("ago.batch.size", batch.Count);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connection).Options;
        await using var db = new AgoChatDbContext(dbOptions);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        var conversations = new ConversationRepository(db);
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        var pendingSuccesses = new List<(InboundMessage Item, int Sequence)>();

        foreach (var group in batch.GroupBy(i => i.Message.ConversationId))
        {
            var conversation = await conversations.GetByIdAsync(group.Key, cancellationToken);
            foreach (var item in group)
            {
                if (conversation is null)
                {
                    item.Ack.TrySetResult(ConversationErrors.NotFound(group.Key.Value));
                    continue;
                }

                // `5-03`: validated read-only, before either aggregate is touched - an invalid
                // attachment reference must not burn a sequence number on Conversation nor mutate
                // Attachment at all. EF's identity map is what makes the "already linked" check safe
                // even within one batch: a second item in this same flush referencing the same
                // attachment re-resolves the *same* tracked instance the first item's LinkToMessage
                // (below) already mutated, not a stale read.
                Attachment? attachment = null;
                if (item.Message.AttachmentId is { } attachmentId)
                {
                    attachment = await db.Attachments.FirstOrDefaultAsync(a => a.Id == attachmentId, cancellationToken);
                    if (attachment is null)
                    {
                        item.Ack.TrySetResult(ConversationErrors.AttachmentNotFound(attachmentId.Value));
                        continue;
                    }

                    if (attachment.ConversationId != conversation.Id)
                    {
                        item.Ack.TrySetResult(ConversationErrors.Forbidden(
                            $"Attachment {attachmentId.Value} does not belong to this conversation."));
                        continue;
                    }

                    if (attachment.State != AttachmentState.Ready || attachment.MessageId is not null)
                    {
                        item.Ack.TrySetResult(ConversationErrors.AttachmentNotReady(
                            $"Attachment {attachmentId.Value} is not available to reference."));
                        continue;
                    }
                }

                var now = clock.UtcNow;
                var messageId = new MessageId(idGenerator.NewId(now));
                try
                {
                    // `14-06`: Content is forwarded verbatim and never inspected - it was validated
                    // for shape by the send handler and is meaningless to everything from here down.
                    var message = item.Message.AuthorKind == MessageAuthorKind.Visitor
                        ? conversation.AddVisitorMessage(
                            new VisitorId(item.Message.AuthorId), messageId, item.Message.Body, now,
                            item.Message.AttachmentId, item.Message.ClientMessageId, item.Message.Content)
                        : conversation.AddOperatorMessage(
                            new OperatorId(item.Message.AuthorId), messageId, item.Message.Body, now,
                            item.Message.AttachmentId, item.Message.ClientMessageId, item.Message.Content);

                    // `5-07`: a returned Message.Id that does not match the id just generated above
                    // means Conversation.AddMessage found an existing message with the same
                    // ClientMessageId and handed that back instead of appending - a retry, not a new
                    // send. Ack with its real sequence and stop here: no new domain event was raised
                    // (nothing to enqueue to the outbox), and linking an attachment a second time to
                    // an already-linked message would itself be a real state error, not a no-op.
                    if (message.Id != messageId)
                    {
                        pendingSuccesses.Add((item, message.Sequence));
                        continue;
                    }

                    // Only after the message itself landed in the aggregate - see the read-only
                    // validation above for why Attachment is never mutated on a path that might still
                    // fail.
                    attachment?.LinkToMessage(messageId, conversation.Id);

                    var domainEvent = conversation.DomainEvents.OfType<MessageAdded>().Last();
                    // `7-01`: this item's *own* captured trace context, not activity?.Id (this
                    // batch's own span) - IOutboxWriter.Enqueue's own remarks explain why: a batch
                    // covering several senders must not tag every row with whichever trace happened
                    // to parent the shared DB-write span.
                    outbox.Enqueue(MessageAcceptedMapper.ToEnvelope(domainEvent, idGenerator), item.Message.TraceParent);
                    pendingSuccesses.Add((item, message.Sequence));
                }
                catch (ConversationParticipantMismatchException)
                {
                    item.Ack.TrySetResult(ConversationErrors.Forbidden(
                        item.Message.AuthorKind == MessageAuthorKind.Visitor
                            ? "This visitor is not a participant of this conversation."
                            : "This operator is not assigned to this conversation."));
                }
                catch (InvalidConversationStateException ex)
                {
                    item.Ack.TrySetResult(ConversationErrors.InvalidState(ex.Message));
                }
                catch (InvalidAttachmentStateException ex)
                {
                    // Defensive, not expected to trigger: the read-only checks above already proved
                    // this attachment was Ready, unlinked, and belonged to this conversation.
                    item.Ack.TrySetResult(ConversationErrors.AttachmentNotReady(ex.Message));
                }
            }

            conversation?.ClearDomainEvents();
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Batch write of {Count} message(s) failed - failing every pending ack in this batch.", pendingSuccesses.Count);
            foreach (var (item, _) in pendingSuccesses)
            {
                item.Ack.TrySetResult(ConversationErrors.Unavailable("Failed to save message, try again."));
            }

            return;
        }

        foreach (var (item, sequence) in pendingSuccesses)
        {
            item.Ack.TrySetResult(sequence);
        }
    }
}
