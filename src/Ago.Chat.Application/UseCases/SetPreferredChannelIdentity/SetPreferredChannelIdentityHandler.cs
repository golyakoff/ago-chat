using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SetPreferredChannelIdentity;

/// <summary>
/// `14-13`/`adr/0079` decision 5: an operator overrides today's implicit "whichever channel was heard
/// from most recently" rule with an explicit, durable choice - see <see cref="Visitor.PreferredChannelIdentityId"/>'s
/// own remarks for why the field lives on <see cref="Visitor"/> and validates nothing about itself.
///
/// <para><b>The one invariant this item is named for: "never an arbitrary id."</b> A non-null
/// <see cref="SetPreferredChannelIdentity.ChannelIdentityId"/> is accepted only when it names a real
/// <see cref="ChannelIdentity"/> row that (a) belongs to this site, (b) belongs to *this conversation's
/// own visitor* - not some other visitor's, verified or not - and (c) is still
/// <see cref="ChannelIdentity.Active"/>. Every other case, including "this id belongs to a different
/// visitor entirely", collapses to the identical <see cref="ConversationErrors.ChannelIdentityNotEligibleForPreference"/>
/// - the same "wrong tenant/wrong owner reads like no such row" info-hiding shape
/// <c>ListChannelIdentitiesForVisitorHandler</c>'s own cross-conversation guard already uses, so a
/// caller cannot use this endpoint to probe whether some id belongs to *some* visitor on the site.</para>
///
/// <para><b>Gated on <see cref="Permission.ConversationSend"/>, not a channel-management permission -
/// the same reasoning <c>RequestChannelLinkFromConsoleHandler</c>'s own remarks give for its sibling
/// action in this same ADR.</b> Preferring a channel only ever redirects a reply the operator was
/// already trusted to send in this conversation, and only among identities that were already verified
/// by `14-12`'s own evidence-based linking - it grants no new access to a channel credential and no
/// ability to read another visitor's history. Unlike <c>UnlinkChannelIdentityHandler</c>'s dedicated,
/// nobody-by-default <see cref="Permission.ChannelIdentityUnlink"/>, this action is reversible and
/// changes nothing about which identities exist, only which one future replies prefer - the smaller
/// blast radius <see cref="Permission"/>'s own granular-by-design vocabulary is built to let a
/// dedicated permission be skipped for, not just added for.</para>
///
/// <para><b>Two access checks, matching <c>ListChannelIdentitiesForVisitorHandler</c> exactly</b>
/// (`adr/0016`'s split: RBAC answers "may this operator send messages at all for this site", the
/// per-conversation comparison answers "may this operator act on *this* one").</para>
/// </summary>
public sealed class SetPreferredChannelIdentityHandler(
    IConversationRepository conversations,
    IChannelIdentityRepository identities,
    IVisitorRepository visitors,
    IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(SetPreferredChannelIdentity command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages in this conversation.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null || conversation.SiteId != command.SiteId)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.OperatorId != command.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        if (command.ChannelIdentityId is { } channelIdentityId)
        {
            var identity = await identities.GetByIdAsync(channelIdentityId, cancellationToken);
            if (identity is null
                || identity.SiteId != command.SiteId
                || identity.VisitorId != conversation.VisitorId
                || !identity.Active)
            {
                return ConversationErrors.ChannelIdentityNotEligibleForPreference(channelIdentityId.Value);
            }
        }

        var visitor = await visitors.GetByIdAsync(conversation.VisitorId, cancellationToken);
        if (visitor is null)
        {
            // Should not happen: a conversation exists for this visitor, so the visitor does - the
            // identical defensive shape DeliverChannelMessageHandler's own remarks describe for its
            // "conversation without a visitor" case.
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        visitor.SetPreferredChannelIdentity(command.ChannelIdentityId);
        await visitors.SaveAsync(visitor, cancellationToken);

        return Result.Success();
    }
}
