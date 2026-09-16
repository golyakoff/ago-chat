using System.Diagnostics;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres.Pipeline;

/// <summary>
/// `4-05`: the actual Postgres write - one connection, one transaction, one
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> call covering every conversation
/// touched by one attempt, mirroring <c>Ago.Chat.Worker.OperatorConversationReleaser</c>'s own
/// "build one <c>AgoChatDbContext</c> on the batch's own transaction via
/// <c>Database.UseTransactionAsync</c>" shape (`4-04`) rather than going through
/// <c>IConversationRepository.SaveAsync</c> per message, which would call
/// <c>SaveChangesAsync</c> once per conversation instead of once per batch. `adr/0005`'s
/// outbox-in-the-same-transaction rule applies to each attempt as a whole: every row's outbox entry
/// commits with every row, or none of them do.
///
/// Grouped by <see cref="ConversationId"/> so a batch containing several messages for the same
/// conversation (concurrent senders in one conversation, naturally coalesced by
/// <c>ConversationSequencer</c> across flushes but not *within* one) still loads that conversation
/// only once and applies its messages in their original relative order - <c>LINQ</c>'s
/// <c>GroupBy</c> preserves within-group order, which is what keeps the resulting `sequence`
/// gap-free ascending for that conversation even inside a single attempt.
///
/// Each message's own domain-invariant failure (participant mismatch, wrong state, conversation not
/// found) fails only that message's own ack, immediately - it does not depend on whether the attempt's
/// eventual commit succeeds, since a rejected message was never staged for persistence at all. A
/// batch-wide failure (the commit itself throwing) no longer fails every message that *was* staged the
/// instant it happens - see <see cref="FlushAsync"/>'s own remarks (`25-109`) for the bounded retry
/// and per-conversation fallback that now sit around <see cref="FlushBatchAsync"/> before any message
/// still pending is told to give up.
/// </summary>
public sealed class MessageBatchWriter(
    NpgsqlDataSource dataSource, IClock clock, IIdGenerator idGenerator, ICache cache,
    IOptions<SiteActivityWatchdogOptions> watchdogOptions, ILogger<MessageBatchWriter> logger)
{
    /// <summary>
    /// `25-109`: 3, matching <c>TransferConversationHandler.TransactionAttempts</c>'s own bound and
    /// its identical jittered backoff formula below - not independently measured for this call site,
    /// but a proven shape against the same class of contention (a concurrent writer bumping a
    /// `conversations` row's own `xmin` mid-transaction) in this exact codebase, which CLAUDE.md rule
    /// 7 treats as the more defensible choice over inventing a new, unmeasured bound here.
    /// </summary>
    private const int MaxAttempts = 3;

    /// <summary>
    /// `25-109`: `MessageBatchWriter` is the one conversation-write path in this codebase that
    /// bypasses <c>ConversationRepository.SaveAsync</c> (which already translates a losing
    /// <c>DbUpdateConcurrencyException</c> into <c>ConversationConcurrencyConflictException</c> and
    /// lets the caller reload-and-reapply - <c>RouteConversationToModuleHandler
    /// .AddSystemMessageAndSaveAsync</c> is the established pattern) and instead calls
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> directly on a batch spanning
    /// several conversations at once. Before this item, its catch-all had no retry at all, so one
    /// lost row failed every other, unrelated message staged in the same flush - confirmed live as
    /// the dominant cause of a 9-18% message-error rate under realistic single-replica load
    /// (`25-109`'s own root-cause finding: <c>Ago.Chat.Worker</c>'s <c>UnreadCounterConsumer</c>,
    /// racing this method's own several-hundred-millisecond, multi-conversation load window on every
    /// single message). `25-109`'s other change (<c>RecordUnreadMessageHandler</c> moving off the
    /// aggregate) removes that dominant racer, but does not make this method retry-proof on its own -
    /// <c>ConversationAssignmentJob</c>, <c>AutoCloseInactiveConversationsJob</c>, module routing and
    /// offline auto-reply remain legitimate cross-process writers of the same row, all far rarer, all
    /// still capable of losing this race on an unlucky attempt.
    ///
    /// <para><b>The retry unit is the whole attempt, not a statement.</b> Connection, transaction and
    /// <see cref="AgoChatDbContext"/> are all recreated by <see cref="FlushBatchAsync"/> on every call
    /// - the context is poisoned after a failed <c>SaveChangesAsync</c>, so nothing from a failed
    /// attempt is safely reusable. Nothing commits on a failed attempt either (the whole transaction
    /// rolls back with it), so replaying is safe: sequences recompute from a freshly reloaded
    /// aggregate, and <c>ClientMessageId</c> dedup (<see cref="Conversation.AddMessage"/>'s own
    /// remarks) plus the <c>(conversation_id, sequence, site_id)</c> unique index are the existing
    /// backstops against a duplicate landing twice even so.</para>
    ///
    /// <para><b>Only what is still actually waiting gets replayed.</b> A domain-level rejection
    /// (participant mismatch, invalid state, attachment not found or not ready, conversation not
    /// found) completes its own ack immediately, inside <see cref="FlushBatchAsync"/>, independent of
    /// whether that attempt's eventual commit succeeds - it was never staged for persistence, so a
    /// failed commit changes nothing about its verdict. Recomputing <c>pending</c> from
    /// <paramref name="batch"/> on every attempt, filtered to <c>!item.Ack.Task.IsCompleted</c>, is
    /// what keeps a message already given its answer from being re-evaluated against conversation
    /// state that has moved on since - exactly the item's own Scope.</para>
    ///
    /// <para><b>The final attempt falls back to one transaction per conversation, not one more retry
    /// of the whole batch.</b> A batch that keeps losing after <see cref="MaxAttempts"/> attempts is
    /// most plausibly losing on account of one persistently-contended conversation, not all of them -
    /// <see cref="FlushEachConversationSeparatelyAsync"/> isolates each conversation group into its
    /// own attempt so the rest of an otherwise-healthy batch is not held hostage by the one that keeps
    /// losing, matching the isolation every other conversation writer in this codebase already gets
    /// from going through <c>ConversationRepository.SaveAsync</c> one aggregate at a time.</para>
    /// </summary>
    internal async Task FlushAsync(IReadOnlyList<InboundMessage> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        // `7-01`: nfr.md's "DB" stage - one span covering the whole flush, every attempt and the
        // fallback included (this is a genuine batch write: several messages, possibly from several
        // different senders' own traces, land in one transaction/one SaveChangesAsync per attempt).
        // Real Npgsql instrumentation nests its own per-command spans inside this one for free, since
        // Activity.Current is what it checks. Parenting is honest about the batching-vs-tracing
        // tension rather than pretending it away: the first item's own trace becomes this span's real
        // parent (correct, and the only case nfr.md's Done-when actually tests - a batch of one),
        // every other item in the same flush gets an ActivityLink instead of a false second parent -
        // OTel's own documented shape for "this span was influenced by, but is not a child of,
        // several other traces." Each row's own outbox entry still gets *its own* correct trace
        // context below, independent of this span's parent - see IOutboxWriter.Enqueue's own remarks
        // for why that has to be explicit, not read from this ambient activity.
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

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var pending = batch.Where(item => !item.Ack.Task.IsCompleted).ToList();
            if (pending.Count == 0)
            {
                // Every item already has an answer - either a prior attempt's domain rejection, or
                // (were this reachable) nothing was ever staged. Nothing left to persist.
                return;
            }

            try
            {
                await FlushBatchAsync(pending, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFlushFailure(ex, pending.Count, attempt);

                if (attempt == MaxAttempts)
                {
                    await FlushEachConversationSeparatelyAsync(pending, cancellationToken);
                    return;
                }

                // `25-109`: the identical jittered formula TransferConversationHandler's own retry
                // loop uses, for the identical reason - a bare retry with no backoff re-issues the
                // losing attempt into the same contended row at the same instant a concurrent
                // writer's own retry might, recreating the next collision instead of escaping it.
                await Task.Delay(
                    TimeSpan.FromMilliseconds(Random.Shared.Next(4, 16) * attempt), cancellationToken);
            }
        }
    }

    /// <summary>
    /// `25-109`: the final-attempt fallback - one connection/transaction/`SaveChangesAsync` per
    /// conversation group in <paramref name="pending"/>, rather than one more attempt at the whole
    /// batch together. Isolates a single persistently-contended conversation from every other,
    /// unrelated message that happened to be batched alongside it: a group that still fails here fails
    /// only its own acks, and a group that succeeds does so independently of whether its neighbours
    /// do. No further retry within this fallback - <see cref="FlushAsync"/> already spent
    /// <see cref="MaxAttempts"/> attempts on the batch as a whole before reaching here.
    /// </summary>
    private async Task FlushEachConversationSeparatelyAsync(
        IReadOnlyList<InboundMessage> pending, CancellationToken cancellationToken)
    {
        foreach (var group in pending.GroupBy(item => item.Message.ConversationId))
        {
            var groupItems = group.ToList();
            try
            {
                await FlushBatchAsync(groupItems, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFlushFailure(ex, groupItems.Count, attempt: MaxAttempts + 1);
                foreach (var item in groupItems.Where(i => !i.Ack.Task.IsCompleted))
                {
                    item.Ack.TrySetResult(ConversationErrors.Unavailable("Failed to save message, try again."));
                }
            }
        }
    }

    private void LogFlushFailure(Exception ex, int itemCount, int attempt)
    {
        // `25-109`: ex.Entries (entity type + primary key), not just the exception's own message -
        // this item's own research pass found the dominant conflict this way (UnreadCounterConsumer
        // racing this method's own load window) but flagged a second, unconfirmed candidate
        // (ConversationAssignmentJob) that only logging the conflicting row's own identity can rule
        // in or out on a future occurrence, rather than guessing from the exception type alone.
        logger.LogError(
            ex,
            "Batch write of {Count} message(s) failed on attempt {Attempt}/{MaxAttempts} - conflicting entities: {Entries}.",
            itemCount, attempt, MaxAttempts, DescribeConflictingEntries(ex));
    }

    private static string DescribeConflictingEntries(Exception ex)
    {
        if (ex is not DbUpdateException dbEx || dbEx.Entries.Count == 0)
        {
            return "n/a";
        }

        return string.Join("; ", dbEx.Entries.Select(entry =>
        {
            var keyValues = entry.Metadata.FindPrimaryKey()?.Properties
                .Select(p => entry.Property(p.Name).CurrentValue?.ToString() ?? "null") ?? [];
            return $"{entry.Metadata.ClrType.Name}({string.Join(",", keyValues)})";
        }));
    }

    /// <summary>
    /// One connection, one transaction, one <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>
    /// covering every conversation touched by <paramref name="pending"/> - <see cref="FlushAsync"/>'s
    /// own retry loop calls this once per attempt with the whole batch still pending, and its fallback
    /// calls it once per conversation group; this method itself does not know or care which. Any
    /// failure from <c>SaveChangesAsync</c>/<c>CommitAsync</c> propagates to the caller rather than
    /// being caught here - deciding what a failure means (retry, fall back, or fail the remaining
    /// acks) is <see cref="FlushAsync"/>'s and <see cref="FlushEachConversationSeparatelyAsync"/>'s
    /// job, not this method's.
    /// </summary>
    private async Task FlushBatchAsync(IReadOnlyList<InboundMessage> pending, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(connection).Options;
        await using var db = new AgoChatDbContext(dbOptions);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);

        var conversations = new ConversationRepository(db);
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        // `13-06`: GetSiteConfigByIdHandler is Application-layer, registered Scoped in ChatModule
        // (it wraps ISiteRepository, which wraps a DbContext) - MessageBatchWriter itself is a
        // Singleton, the same "manage a short-lived AgoChatDbContext per flush rather than take one
        // from DI" shape this method already uses for `conversations`/`outbox` just above (both are
        // `new`'d against this flush's own `db`, never resolved from a container). Injecting the
        // handler itself would be a captive-dependency bug (a Singleton permanently holding a Scoped
        // service's first-ever DbContext); building it fresh, per attempt, from this attempt's own
        // `db` plus the injected `ICache` (a real Singleton - `Ago.Platform.Caching.Redis`'s Redis
        // client is stateless and thread-safe, unlike a DbContext) keeps every dependency at the
        // lifetime it was actually registered with.
        var getSiteConfig = new GetSiteConfigByIdHandler(new SiteRepository(db), cache);
        var pendingSuccesses = new List<(InboundMessage Item, int Sequence)>();
        // `23-73`: every distinct site that had at least one operator-authored message actually accepted
        // into its conversation this attempt - "accepted", not "attempted", so a message a participant-
        // mismatch/invalid-state catch below rejects never counts as operator activity. Touched once,
        // batched, right before this transaction commits - see this method's own closing remarks.
        var touchedSiteIds = new HashSet<Guid>();

        // `25-109` follow-up: one round trip for every conversation this attempt touches, not one per
        // group - GetByIdsAsync's own remarks explain why this matters beyond raw round-trip count:
        // it shrinks the window between "loaded" and "committed" that a concurrent writer of the same
        // row (ConversationAssignmentJob and friends, per this item's own root-cause finding) can land
        // in, which is the actual race this whole retry mechanism exists to recover from.
        var conversationIds = pending.Select(i => i.Message.ConversationId).Distinct().ToList();
        var conversationsById = await conversations.GetByIdsAsync(conversationIds, cancellationToken);

        foreach (var group in pending.GroupBy(i => i.Message.ConversationId))
        {
            conversationsById.TryGetValue(group.Key, out var conversation);

            // Resolved once per conversation, not once per message - the site's tier does not change
            // mid-batch, and this is a cache-aside read (adr/0031's own carve-out from CLAUDE.md rule
            // 8: a stamp, not a gate). A site whose config cannot be resolved at all - the site was
            // deleted in the instant between the conversation loading and this read, the one race this
            // handler cannot see coming - stamps RetentionClass.Free rather than failing the whole
            // group: an impossible-in-practice edge case getting the safest (shortest-lived) class is
            // preferable to an already-validated batch of sends failing on a lookup that has nothing
            // to do with whether they are valid messages.
            //
            // `23-64`: the DTO itself is kept, not just its `Tier`, for the identical cache-aside read
            // - `SiteConfigDto`'s own remarks state the same `adr/0031` carve-out for
            // `WidgetAutoOpenEnabled`/`WidgetAutoOpenGreetingText` below: whether *this* message
            // materialises a greeting beside it is a stamp, not a gate, so reusing this one already-
            // loaded read (rather than a second lookup) costs nothing rule 8 protects against.
            var siteConfig = conversation is null
                ? null
                : await getSiteConfig.HandleAsync(new GetSiteConfigById(conversation.SiteId), cancellationToken);
            var retentionClass = conversation is null
                ? (RetentionClass?)null
                : RetentionClass.FromTier(siteConfig?.Tier ?? RetentionClass.Free.Value);

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
                    // `23-64`/`adr/0148`: materialised, if it applies at all, immediately before the
                    // visitor's own message that requested it - inside the same transaction, so the
                    // greeting can never exist without the real message that follows it committing
                    // alongside it (or vice versa: the whole `SaveChangesAsync` below either lands
                    // both rows or neither). `Conversation.AddAutoGreetingMessage`'s own `_messages.Count
                    // > 0` guard is what actually decides "genuinely the first message" - re-checked
                    // fresh here, against this flush's own freshly-loaded aggregate, never trusted from
                    // whatever the widget believed when it drew the greeting seconds or minutes earlier.
                    // `siteConfig` is re-checked too (`WidgetAutoOpenEnabled`/`WidgetAutoOpenGreetingText`
                    // as they stand *now*, `adr/0148`'s own "the tenant's configuration as it stands
                    // today" consequence) - a tenant who disabled auto-open or cleared the greeting in
                    // the interval between the widget drawing it and this write gets no greeting
                    // materialised, silently, which is the correct outcome per that ADR, not a bug to
                    // guard against.
                    if (item.Message.AuthorKind == MessageAuthorKind.Visitor
                        && item.Message.MaterializeAutoGreeting
                        && siteConfig is { WidgetAutoOpenEnabled: true, WidgetAutoOpenGreetingText: { } greetingText })
                    {
                        var greetingMessageId = new MessageId(idGenerator.NewId(now));
                        var greeting = conversation.AddAutoGreetingMessage(
                            greetingMessageId, new MessageBody(greetingText), now, retentionClass);
                        if (greeting is not null)
                        {
                            var greetingEvent = conversation.DomainEvents.OfType<MessageAdded>().Last();
                            // No caller is waiting on this row's own ack - nothing in `batch` represents
                            // it, and nothing should: the greeting was never a request anyone made, only
                            // a consequence of the one that follows. Its outbox row still carries the
                            // same `TraceParent` as the real message beside it, so both land in the one
                            // trace a reviewer would look at to understand this send.
                            outbox.Enqueue(MessageAcceptedMapper.ToEnvelope(greetingEvent, idGenerator), item.Message.TraceParent);
                        }
                    }

                    // `14-06`: Content is forwarded verbatim and never inspected - it was validated
                    // for shape by the send handler and is meaningless to everything from here down.
                    var message = item.Message.AuthorKind == MessageAuthorKind.Visitor
                        ? conversation.AddVisitorMessage(
                            new VisitorId(item.Message.AuthorId), messageId, item.Message.Body, now,
                            item.Message.AttachmentId, item.Message.ClientMessageId, item.Message.Content, retentionClass)
                        : conversation.AddOperatorMessage(
                            new OperatorId(item.Message.AuthorId), messageId, item.Message.Body, now,
                            item.Message.AttachmentId, item.Message.ClientMessageId, item.Message.Content, retentionClass);

                    if (item.Message.AuthorKind == MessageAuthorKind.Operator)
                    {
                        // `23-73`: the inactivity watchdog's own reset hook. Counted here, once the
                        // domain call above has actually accepted the message (no
                        // ConversationParticipantMismatchException/InvalidConversationStateException
                        // thrown) - a message this conversation refused is not evidence an operator
                        // reached a real customer. Deliberately counted for the retry-duplicate branch
                        // just below too, not only a genuinely new message: a retried send still proves
                        // this operator's client is live and answering, which is what the watchdog
                        // exists to detect.
                        touchedSiteIds.Add(conversation.SiteId.Value);
                    }

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

        if (touchedSiteIds.Count > 0)
        {
            // `23-73`: raw SQL against this attempt's own already-open connection/transaction, not
            // through ISiteActivityWatchdog - that port opens its own connection for the one caller
            // that has none open yet (OperatorIdentityClaimsTransformation); this attempt already has
            // one, and reusing it is what makes this write commit or roll back atomically with every
            // message it was touched by, for free, rather than as a second, independent write that
            // could land even if the message batch itself then fails to commit. One statement for
            // every touched site in this attempt (TouchManyAsync), not one round trip per site - a
            // single attempt can carry operator messages for several different sites at once.
            await SiteActivityWatchdogQuery.TouchManyAsync(
                connection, transaction, touchedSiteIds, clock.UtcNow, watchdogOptions.Value.MinTouchInterval,
                cancellationToken);
        }

        // `25-109`: no longer caught here - a failure propagates to FlushAsync's retry loop (or
        // FlushEachConversationSeparatelyAsync's own per-group catch), which is what decides whether
        // to retry, fall back, or finally fail the remaining acks. Nothing below this point runs if
        // either call throws, so nothing in pendingSuccesses is acknowledged for an attempt that did
        // not actually commit.
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var (item, sequence) in pendingSuccesses)
        {
            item.Ack.TrySetResult(sequence);
        }
    }
}
