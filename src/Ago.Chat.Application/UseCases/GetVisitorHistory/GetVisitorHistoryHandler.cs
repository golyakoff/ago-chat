using Ago.Chat.Application.Abstractions;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetVisitorHistory;

/// <summary>
/// `18-07`: an operator's "has this person talked to us before" panel - `18-06`'s Depends-on note is
/// why this exists at all (auto-close turns a returning channel visitor's history into several
/// `Closed` rows instead of one open one nobody ever revisits).
///
/// <para><b>Two access checks, matching <see cref="GetConversationHistory.GetConversationHistoryHandler"/>'s
/// own operator entry point exactly</b> (`adr/0016`'s split, restated in this item's own Context: RBAC
/// answers "may this operator read conversations at all for this site", the per-conversation
/// comparison answers "may this operator read *this* one"). The second check is what makes this a
/// scoped capability rather than the site-wide visitor lookup the backlog item's own Out-of-scope
/// section names as a different, unbuilt feature (`18-01`) - an operator not assigned to
/// <see cref="GetVisitorHistory.ConversationId"/> gets the same <c>Conversation.Forbidden</c> a
/// stranger to that conversation gets from every other operator-scoped handler in this codebase, not
/// a narrower "you may not see history" code, because from this handler's point of view it is the
/// same failure: not a party to the conversation that would identify the visitor at all.</para>
///
/// <para><b>The gate lives here, not in the read store.</b> <see cref="IChannelIdentityRepository.FindMostRecentForVisitorAsync"/>
/// already exists (`14-02`) and already answers "does this visitor have a channel identity" - reusing
/// it, rather than teaching <see cref="IConversationReadStore.GetVisitorHistoryAsync"/> a
/// <c>channel_identities</c> join of its own, keeps the structural precondition (`14-01`'s model: a
/// widget visitor has no <see cref="ChannelIdentity"/> row, ever) where the write-side aggregate for
/// that exact question already lives, and means a widget visitor never reaches the paginated read at
/// all - not even to discover it returns nothing.</para>
///
/// <para><b><see cref="HandleHistoricalConversationAsOperatorAsync"/> is a second, deliberately
/// separate access rule, found while wiring "opening one shows its real message history" - the
/// backlog item's own Done-when.</b> The obvious move was to reuse
/// <see cref="GetConversationHistory.GetConversationHistoryHandler.HandleAsOperatorAsync"/>, since it
/// already reads message history for an operator. It cannot serve this call: its per-conversation
/// check is <c>conversation.OperatorId == RequestedBy</c>, and a past, `Closed` conversation's
/// <see cref="Conversation.OperatorId"/> is frozen at whichever operator last held it (`Close` never
/// clears it) - so only the operator who originally handled that exact past conversation could ever
/// open it, which is precisely the case this feature exists to cross. The correct rule is different
/// and new: an operator may read a historical conversation if they are assigned to *some other*,
/// live conversation with the *same visitor* - proven by comparing
/// <see cref="Conversation.VisitorId"/> on both rows, not by an assignment on the historical one
/// (which the requesting operator may never have held). This is a genuinely new way a message becomes
/// visible to an operator who was never a party to the conversation that contains it -
/// `docs/architecture/personal-data.md` is updated in this same change to say so, per this backlog
/// item's own Context section and `16-02`'s erasure guarantees, which must keep covering it.</para>
///
/// <para><b>`24-12`: <see cref="HandleHistoricalConversationAsOperatorAsync"/>'s own success path now
/// writes an <c>access_records</c> row; <see cref="HandleAsOperatorAsync"/> deliberately does not.</b>
/// The historical read is the one this codebase already singles out, in the paragraph directly above,
/// as "a message becomes visible to an operator who was never a party to the conversation" - the exact
/// boundary-crossing read `24-12`'s own Scope names. The summary list one paragraph up returns only
/// previews of conversations the same visitor had, gated on the identical two checks; recording both
/// would be two rows for what a reader experiences as one action ("show me this visitor's history,
/// then let me open one"), and `24-12`'s own Scope warns against exactly that kind of doubling
/// ("recording everything is a second copy of the traffic"). The row is written only after every
/// check above has already passed - a <c>Forbidden</c>/<c>NotFound</c> return never reaches
/// <see cref="IAccessRecordRepository.RecordAsync"/>, per this item's own "a read that fails
/// authorisation is not an access."</para>
/// </summary>
public sealed class GetVisitorHistoryHandler(
    IConversationRepository conversations,
    IConversationReadStore readStore,
    IChannelIdentityRepository channelIdentities,
    IPermissionChecker permissions,
    IAccessRecordRepository accessRecords,
    IClock clock,
    IIdGenerator idGenerator)
{
    public async Task<Result<VisitorHistoryResponse>> HandleAsOperatorAsync(
        GetVisitorHistory query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var conversation = await conversations.GetByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        if (conversation.OperatorId != query.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        // `14-01`'s structural gate: a widget visitor has no ChannelIdentity row, ever (see this
        // type's own remarks) - short-circuit before the paginated read runs at all, so a widget
        // visitor's conversation never even queries for history it structurally cannot have.
        var identity = await channelIdentities.FindMostRecentForVisitorAsync(conversation.VisitorId, cancellationToken);
        if (identity is null)
        {
            return new VisitorHistoryResponse(HasChannelIdentity: false, Conversations: [], NextBeforeId: null);
        }

        var page = await readStore.GetVisitorHistoryAsync(
            conversation.VisitorId, query.ConversationId, query.BeforeId, query.PageSize, cancellationToken);

        return new VisitorHistoryResponse(
            HasChannelIdentity: true, page.Conversations.Select(ToDto).ToList(), page.NextBeforeId);
    }

    /// <summary>"Open one" - see this type's own remarks for why this is not a second caller of
    /// <see cref="GetConversationHistory.GetConversationHistoryHandler"/>.</summary>
    public async Task<Result<ConversationHistoryPage>> HandleHistoricalConversationAsOperatorAsync(
        GetVisitorHistoryConversation query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var conversation = await conversations.GetByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        if (conversation.OperatorId != query.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        var historical = await conversations.GetByIdAsync(query.HistoricalConversationId, cancellationToken);
        if (historical is null)
        {
            return ConversationErrors.NotFound(query.HistoricalConversationId.Value);
        }

        // The actual authorization boundary for this call - not an assignment on `historical` (the
        // requesting operator may never have held one), but that it belongs to the same visitor as a
        // conversation this operator *is* currently, legitimately serving. See this type's own
        // remarks for why GetConversationHistoryHandler's ordinary per-conversation check cannot
        // stand in for this one.
        if (historical.VisitorId != conversation.VisitorId)
        {
            return ConversationErrors.Forbidden("This conversation does not belong to the same visitor.");
        }

        var page = await readStore.GetHistoryAsync(
            query.HistoricalConversationId, historical.SiteId, query.BeforeSequence, query.PageSize, cancellationToken);

        // `24-12`: only now, after every check above has actually let the read through - see this
        // type's own remarks for why this call and not HandleAsOperatorAsync's own list is the one
        // that writes a row, and why a Forbidden/NotFound return anywhere above never reaches this
        // line. GetHistoryAsync itself has no failure branch once `historical` is loaded (it reads the
        // one conversation this method has already confirmed exists and belongs to this visitor), so
        // reaching this line already means the read succeeded.
        var now = clock.UtcNow;
        await accessRecords.RecordAsync(
            new AccessRecordToWrite(
                idGenerator.NewId(now),
                now,
                AccessRecordKind.CrossConversationHistoryRead,
                historical.SiteId,
                AccessRecordActorKind.Operator,
                query.RequestedBy.Value.ToString(),
                AccessRecordResourceKind.Conversation,
                query.HistoricalConversationId.Value),
            cancellationToken);

        return page;
    }

    private static VisitorHistoryConversationDto ToDto(VisitorHistoryItem item) => new(
        item.Id.Value, item.State, item.StartedAt, item.ClosedAt,
        item.PreviewBody, item.PreviewAuthorKind?.ToString(), item.PreviewCreatedAt);
}
