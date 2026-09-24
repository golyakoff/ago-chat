using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetOperatorQueue;

/// <summary>
/// `5-07`: the console's queue/dashboard view needs a way to learn "what's waiting for my site, what's
/// assigned to me" on load and after a page refresh - a real gap found while building the console, not
/// anticipated by any earlier item: `4-02`'s automatic assignment engine notifies a *connected*
/// operator of a new assignment over the hub (`"ConversationAssigned"`, `ResolveConversationAssignmentTargetsHandler`),
/// but nothing answers "what do I already have" for an operator who just opened the console. A pure
/// query, no Domain step - it neither raises nor enforces a business invariant, only reads two lists
/// `IConversationRepository` already knows how to produce (`GetWaitingForSiteAsync`,
/// `GetAssignedToOperatorAsync` - `4-04`'s existing method, reused here rather than duplicated).
///
/// <para><b>`18-04`'s tag filter, applied in-memory rather than pushed into the repository query.</b>
/// <see cref="ITagRepository"/> shares no query surface with <see cref="IConversationRepository"/> - it
/// answers "which conversation ids carry this tag" as its own small, bounded set
/// (<see cref="ITagRepository.GetConversationIdsForTagAsync"/>), which this handler intersects against
/// the two lists it already loaded. That is the same shape <see cref="IConversationRepository"/>'s own
/// remarks already justify for those two reads (small, bounded, unpaginated) - adding a join to the EF
/// query would touch a write-side port for a read-only filter, and threading a new parameter through
/// <see cref="IConversationRepository.GetAssignedToOperatorAsync"/> would also change
/// <c>OperatorConversationReleaser</c>'s unrelated call to it for no reason. `GetAllConversationsForSiteHandler`
/// makes the opposite call for its own genuinely paginated read - see that handler's own remarks.</para>
///
/// <para><b>`25-59`: one tag widened to several, AND rather than OR.</b> <see cref="ITagRepository"/>
/// still answers only "which conversation ids carry *this one* tag" - there is no multi-tag overload
/// to add, because AND-ing several single-tag sets is exactly set intersection, computed here for the
/// same reason the single-tag case was computed here: this handler already holds the two small lists
/// in memory, and <see cref="ITagRepository"/> has no reason to grow a second query shape for a
/// filter its own caller can already assemble from the one it has. One repository round trip per
/// selected tag, intersected as it goes - acceptable because the tag vocabulary a filter realistically
/// selects from is small (`Tag.MaxNameLength`'s own "browsable, not evaluated per message" remarks) and
/// this is a query, not a hot per-message path.</para>
/// </summary>
public sealed class GetOperatorQueueHandler(
    IConversationRepository conversations, IVisitorRepository visitors, ITagRepository tags,
    IPermissionChecker permissions, IVisitorContactDetailRepository contactDetails,
    IConversationReadStore readStore)
{
    public async Task<Result<OperatorQueueResponse>> HandleAsync(GetOperatorQueue query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.OperatorId, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var waiting = await conversations.GetWaitingForSiteAsync(query.SiteId, cancellationToken);
        var assigned = await conversations.GetAssignedToOperatorAsync(query.OperatorId, cancellationToken);

        // `24-10`: the identical in-memory filter this handler's own tag filter right below already
        // uses, not a change to IConversationRepository's own query - GetAssignedToOperatorAsync also
        // backs OperatorConversationReleaser's disconnect-grace-period release (`4-04`), a write-side
        // process this item deliberately leaves untouched (see this item's commit-prep notes: filtering
        // a blocked conversation out of that list too would leave its capacity claim never released).
        // This queue view is the only caller that needs a blocked conversation invisible.
        //
        // `23-69`/`23-77`: `!c.IsRoutingSuppressed` joins the identical filter - a conversation a
        // restricted visitor silently opened must never appear in an operator's own queue either, the
        // same "an operator never sees it" requirement `IsBlocked` is filtered for right here already.
        // See `Conversation.RoutingSuppressedAt`'s own remarks for why this is a second flag rather
        // than reusing `IsBlocked` itself.
        waiting = waiting.Where(c => !c.IsBlocked && !c.IsRoutingSuppressed).ToList();
        assigned = assigned.Where(c => !c.IsBlocked && !c.IsRoutingSuppressed).ToList();

        if (query.Tags is { Count: > 0 } tagIds)
        {
            IReadOnlySet<ConversationId>? matchingEveryTagSoFar = null;
            foreach (var tagId in tagIds)
            {
                var taggedIds = await tags.GetConversationIdsForTagAsync(tagId, query.SiteId, cancellationToken);
                matchingEveryTagSoFar = matchingEveryTagSoFar is null
                    ? taggedIds
                    : matchingEveryTagSoFar.Intersect(taggedIds).ToHashSet();
            }

            waiting = waiting.Where(c => matchingEveryTagSoFar!.Contains(c.Id)).ToList();
            assigned = assigned.Where(c => matchingEveryTagSoFar!.Contains(c.Id)).ToList();
        }

        // `25-56`: this handler loads full Conversation aggregates (this DTO's own remarks explain
        // why), which carry a VisitorId but never the Visitor itself - one batch read for every
        // distinct visitor across both lists, not a Visitor lookup bolted onto Conversation
        // (IVisitorRepository.GetManyByIdsAsync's own remarks on why a batch, not a loop).
        var visitorIds = waiting.Concat(assigned).Select(c => c.VisitorId).Distinct().ToList();
        var visitorsById = await visitors.GetManyByIdsAsync(visitorIds, cancellationToken);

        // `25-56`'s own second half: the identical one-batch-not-a-loop reasoning as the emoji lookup
        // right above, against a different repository - a visitor's own name lives in
        // IVisitorContactDetailRepository, not on Visitor itself (that interface's own remarks on
        // GetNamesForVisitorsAsync).
        var namesByVisitorId = await contactDetails.GetNamesForVisitorsAsync(visitorIds, cancellationToken);

        // `26-29`: the identical one-batch-not-a-loop shape as the visitor/name lookups right above,
        // against IConversationReadStore instead - GetHistoryAsync/Conversation.Messages would work
        // per-conversation, but only by materialising every message of every queued conversation to
        // read the last one of each, which is the unbounded read this method exists to avoid on a
        // screen the console polls. One SiteId for the whole call: every id in either list already
        // belongs to query.SiteId (this handler's own permission check proves `waiting`; an Operator
        // belongs to exactly one Site, so `assigned` does too), which also lets this single query
        // prune `messages`' own site_id hash partitioning to one bucket.
        var conversationIds = waiting.Concat(assigned).Select(c => c.Id).ToList();
        var latestMessagesByConversationId = await readStore.GetLatestMessagesAsync(
            query.SiteId, conversationIds, cancellationToken);

        return new OperatorQueueResponse(
            waiting.Select(c => ToSummary(c, visitorsById, namesByVisitorId, latestMessagesByConversationId)).ToList(),
            assigned.Select(c => ToSummary(c, visitorsById, namesByVisitorId, latestMessagesByConversationId)).ToList());
    }

    private static ConversationSummaryDto ToSummary(
        Conversation conversation, IReadOnlyDictionary<VisitorId, Visitor> visitorsById,
        IReadOnlyDictionary<VisitorId, string> namesByVisitorId,
        IReadOnlyDictionary<ConversationId, LatestMessageSummary> latestMessagesByConversationId)
    {
        // `25-56`: absent only if the visitor row somehow vanished between the two reads (the FK
        // guarantees it exists at the time this conversation was created and Visitor rows are never
        // deleted, so this is a defensive fallback, not an expected path) - null EmojiCreature/EmojiFood
        // is the same "additive, missing for a row this caller could not resolve" shape every other
        // optional field on this DTO already uses.
        var visitor = visitorsById.GetValueOrDefault(conversation.VisitorId);

        // `26-29`: absent whenever this conversation has no messages at all (a Pending conversation,
        // `25-221`, or any other edge case) - LatestMessageSummary's own remarks explain why this
        // dictionary omits rather than placeholders that case.
        var latestMessage = latestMessagesByConversationId.GetValueOrDefault(conversation.Id);

        return new(
            conversation.Id.Value, conversation.VisitorId.Value, conversation.State.ToString(),
            conversation.CreatedAt, conversation.OperatorUnreadCount, conversation.OperatorId?.Value,
            OperatorName: null, conversation.HasAttachmentUploadGrant, conversation.AttachmentUploadGrantedAt,
            conversation.AttachmentUploadGrantedBy?.Value,
            EmojiCreature: visitor?.EmojiCreature, EmojiFood: visitor?.EmojiFood,
            // `25-56`'s own second half: absent whenever this visitor has never given a name - the
            // ordinary case, not a defensive fallback the way the emoji pair's own absence above is.
            VisitorName: namesByVisitorId.GetValueOrDefault(conversation.VisitorId),
            // `26-90`: the truncation/suppression rule this handler owned privately from `26-29`/`26-76`
            // now lives in LastMessagePreviewMapper, because GetAllConversationsForSiteHandler needs the
            // identical rule for the identical field - see that mapper's own remarks. Behaviour unchanged.
            LastMessagePreview: LastMessagePreviewMapper.ToPreview(latestMessage),
            // `26-29`: the timestamp is honest about "when was the last thing said" regardless of
            // whether ToPreview refused to render the words - a system message, an attachment, or a
            // module step still moves this instant forward, exactly as ConversationSummaryDto's own
            // remarks state.
            LastMessageAt: latestMessage?.CreatedAt,
            // `26-76`: plumbing only - LatestMessageSummary already carries this raw value, ToSummary
            // just exposes it on the wire so a client can render its own icon/label rather than the
            // backend guessing at one. See ConversationSummaryDto.LastMessageContentKind's own remarks.
            LastMessageContentKind: latestMessage?.ContentKind);
    }
}
