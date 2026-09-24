using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetAllConversationsForSite;

/// <summary>
/// `5-08`: the admin/supervisor role's distinguishing feature per `authorization.md` - "sees every
/// conversation for a site (not just its own assigned ones)". Gated on
/// <see cref="Permission.SiteConfigure"/>, not <see cref="Permission.ConversationRead"/> -
/// `ConversationRead` is what every ordinary operator already holds and only ever unlocks their own
/// assigned/waiting-queue view (`GetOperatorQueueHandler`); this handler intentionally does not
/// extend that check to be site-wide, since doing so would let any ordinary operator read this list
/// too, defeating the reason `authorization.md` named a separate admin role in the first place.
/// `SiteConfigure` over `SiteManageOperators` because this is a site-oversight read, not an
/// operator-management action - the latter is reserved for a future role-assignment surface this
/// item deliberately does not build (see this item's own commit-prep notes on that decision).
///
/// <para><b>`26-90`: the state filter is parsed here and applied in SQL, never after the page.</b>
/// The order of the two checks below matters and matches <c>GetSiteConsentAcceptancesHandler</c>'s own:
/// permission first, parsing second, so a caller with no standing on this site learns nothing about
/// whether the states they sent were even valid. Unknown values are refused rather than dropped - a
/// silently ignored typo would answer a question the caller did not ask (every state, looking exactly
/// like a filter that worked), which on a list whose whole point is "show me only the closed ones" is
/// worse than an error.</para>
/// </summary>
public sealed class GetAllConversationsForSiteHandler(
    IConversationReadStore readStore, IPermissionChecker permissions)
{
    public async Task<Result<AllConversationsForSiteResponse>> HandleAsync(
        GetAllConversationsForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view every conversation for this site.");
        }

        List<ConversationState>? states = null;
        if (query.States is { Count: > 0 })
        {
            states = [];
            foreach (var raw in query.States)
            {
                // `Enum.IsDefined` as well as `TryParse`, the same pair every other enum-from-the-wire
                // check in this project uses (`UpdateContactVisibilityHandler`'s own remarks): TryParse
                // alone accepts any integer whose value is not a member at all.
                if (!Enum.TryParse<ConversationState>(raw, ignoreCase: true, out var state) || !Enum.IsDefined(state))
                {
                    return ConversationErrors.InvalidState(
                        $"'{raw}' is not a valid conversation state - expected one of "
                        + $"'{nameof(ConversationState.Pending)}', '{nameof(ConversationState.Waiting)}', "
                        + $"'{nameof(ConversationState.Assigned)}' or '{nameof(ConversationState.Closed)}'.");
                }

                states.Add(state);
            }
        }

        var page = await readStore.GetAllForSiteAsync(
            query.SiteId, query.BeforeId, query.PageSize, query.Tag, states, cancellationToken);

        return new AllConversationsForSiteResponse(page.Conversations.Select(ToSummary).ToList(), page.NextBeforeId);
    }

    private static ConversationSummaryDto ToSummary(ConversationSummaryItem item) => new(
        item.Id.Value, item.VisitorId.Value, item.State, item.CreatedAt, item.OperatorUnreadCount,
        item.OperatorId?.Value, item.OperatorName,
        // `25-56`: this view has no attachment-upload-grant fields either (ConversationSummaryDto's
        // own remarks on why only GetOperatorQueueHandler's two lists carry those) - named here so the
        // trailing EmojiCreature/EmojiFood arguments below don't silently occupy the wrong positional
        // slot.
        HasAttachmentUploadGrant: false, AttachmentUploadGrantedAt: null, AttachmentUploadGrantedByOperatorId: null,
        EmojiCreature: item.EmojiCreature, EmojiFood: item.EmojiFood, VisitorName: item.VisitorName,
        // `26-90`: the identical rule GetOperatorQueueHandler applies to the identical field, through
        // the same mapper rather than a second copy of it - see LastMessagePreviewMapper's own remarks
        // on why it stopped being that handler's private method.
        LastMessagePreview: LastMessagePreviewMapper.ToPreview(item.LatestMessage),
        LastMessageAt: item.LatestMessage?.CreatedAt,
        LastMessageContentKind: item.LatestMessage?.ContentKind,
        MessageCount: item.MessageCount);
}
