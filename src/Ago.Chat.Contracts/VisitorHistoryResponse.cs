namespace Ago.Chat.Contracts;

/// <summary>
/// `18-07`: `GET /api/v1/conversations/{conversationId}/visitor-history`'s response body.
///
/// <para><b>`26-114`/`adr/0182` removed <c>HasChannelIdentity</c>.</b> Before this item, the field
/// carried the channel-identity gate - <see langword="false"/> meant "this visitor structurally cannot
/// have past-dialog history" (a widget visitor, `14-01`'s model) and the console rendered no panel at
/// all for that case, versus an empty-but-real list for a channel visitor with no priors yet. That gate
/// is gone: every visitor now reaches the identical, widened per-visitor-on-site scope
/// (`docs/design/26-111-thread-contact-detail-panel.md`'s decision #3), so the distinction the field
/// used to carry no longer exists to report - an empty <see cref="Conversations"/> list is now the one
/// and only "nothing to show yet" case, for every visitor alike. A console still rendering the removed
/// field's old gate needs its own follow-up change to show the row unconditionally instead - out of
/// this item's own scope (backend-only, `docs/backlog/26-114-*.md`).</para>
/// </summary>
public sealed record VisitorHistoryResponse(
    IReadOnlyList<VisitorHistoryConversationDto> Conversations,
    Guid? NextBeforeId);
