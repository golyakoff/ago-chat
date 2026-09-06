using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListVisitorContactDetails;

/// <summary>
/// `14-14`: the read behind the console's own contact-details block. Gated on
/// <see cref="Permission.ConversationRead"/>, not the narrower assigned-operator check
/// <see cref="ListChannelIdentitiesForVisitor.ListChannelIdentitiesForVisitorHandler"/> applies for
/// itself - the same reasoning <see cref="GetConversationNotes.GetConversationNotesHandler"/>'s own
/// remarks give for reusing <c>ConversationRead</c> rather than a narrower check: a recorded contact
/// detail is shared operational context for whoever can already read this conversation, including
/// after a transfer, not something scoped to the one operator currently assigned.
///
/// <para>`23-11`/`decisions.md` §5: <b>the masking happens here, in the read model - never in the
/// console.</b> On <see cref="ContactVisibility.MaskedWithReveal"/>, every <see cref="VisitorContactDetailDto.Value"/>
/// this handler returns is already the masked string; the real value never crosses into this
/// response at all, so there is no client-side flag a console build could get wrong and accidentally
/// render the unmasked number. The rung is read via <see cref="GetSiteConfigByIdHandler"/> - composing
/// rather than duplicating (<c>SendOfflineAutoReplyHandler</c>'s own precedent for this exact
/// composition) - which means this read rides the same cache-aside, event-invalidated
/// <c>SiteConfigDto</c> every other per-site setting already uses, not a fresh, uncached
/// <see cref="ISiteRepository"/> load of its own. A site the cache cannot resolve (a lookup miss this
/// handler did not expect, given the conversation above already proved the site exists) is treated as
/// <see cref="ContactVisibility.Visible"/> - the safe default this codebase ships everywhere else, and
/// the one that keeps this handler's own contract "never throws for a site it just proved exists."</para>
/// </summary>
public sealed class ListVisitorContactDetailsHandler(
    IConversationRepository conversations,
    IVisitorContactDetailRepository contactDetails,
    IPermissionChecker permissions,
    GetSiteConfigByIdHandler siteConfig)
{
    public async Task<Result<IReadOnlyList<VisitorContactDetailDto>>> HandleAsync(
        ListVisitorContactDetails query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var conversation = await conversations.GetByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null || conversation.SiteId != query.SiteId)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        // `GetSiteConfigById.GetSiteConfigById`, not a bare `GetSiteConfigById`: the query type shares
        // its name with its own namespace (`SendOfflineAutoReplyHandler`'s own precedent for this
        // exact composition).
        var config = await siteConfig.HandleAsync(new GetSiteConfigById.GetSiteConfigById(query.SiteId), cancellationToken);
        var masked = config?.ContactVisibility == ContactVisibility.MaskedWithReveal;

        var items = await contactDetails.GetForVisitorAsync(conversation.VisitorId, cancellationToken);
        IReadOnlyList<VisitorContactDetailDto> dtos = items
            .Select(d => new VisitorContactDetailDto(
                d.Id.Value, d.Kind.ToString(), masked ? Mask(d.Value) : d.Value, d.RecordedByOperatorId?.Value,
                d.Source.ToString(), d.Verified, d.RecordedAt, masked))
            .ToList();

        return Result<IReadOnlyList<VisitorContactDetailDto>>.Success(dtos);
    }

    /// <summary>Keeps the first two and last two characters and replaces everything between with a
    /// fixed run of bullets - enough that an operator can sanity-check "this looks like a real phone
    /// number" without the masked string ever being enough to dial or write down
    /// (`decisions.md` §5: attribution, not prevention, but the masked value itself must not be the
    /// leak). A short value (four characters or fewer) is masked entirely - keeping any of it would
    /// mean keeping most of it.</summary>
    private static string Mask(string value) =>
        value.Length <= 4
            ? new string('•', value.Length)
            : $"{value[..2]}{new string('•', value.Length - 4)}{value[^2..]}";
}
