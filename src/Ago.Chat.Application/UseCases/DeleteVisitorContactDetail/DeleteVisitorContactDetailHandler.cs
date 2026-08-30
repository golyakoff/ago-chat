using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.DeleteVisitorContactDetail;

/// <summary>
/// `14-14`/`adr/0079` section 6: deletes a mistaken entry. Gated on
/// <see cref="Permission.ConversationSend"/> - the identical reasoning
/// <see cref="RecordVisitorContactDetail.RecordVisitorContactDetailHandler"/>'s own remarks give: there
/// is no separate "unlink"-style permission here (unlike <see cref="Permission.ChannelIdentityUnlink"/>),
/// because there is no routing capability to protect, only a note the same operator population that
/// may record one may also correct.
///
/// <para><b>Why this takes a <see cref="ConversationId"/> rather than <c>UnlinkChannelIdentity</c>'s
/// bare id-plus-site-scoped-route shape.</b> A <see cref="Domain.ChannelIdentity"/> carries its own
/// <see cref="Domain.ChannelIdentity.SiteId"/> column, so <c>UnlinkChannelIdentityHandler</c> can check
/// tenant scope directly against the row it loaded. <see cref="Domain.VisitorContactDetail"/>
/// deliberately does not (this type's own remarks on why it stays a small, few-field type) - so tenant
/// scope here is checked exactly the way <see cref="ConversationNote"/>'s own repository is checked one
/// level up, through the conversation: this handler resolves the conversation first (tenant-checked
/// against <paramref name="command"/>'s <see cref="Domain.SiteId"/>) and then requires the loaded
/// detail's own <see cref="Domain.VisitorContactDetail.VisitorId"/> to match
/// <see cref="Domain.Conversation.VisitorId"/> before deleting anything - a detail belonging to a
/// different visitor (this tenant's own, or another tenant's) reads exactly like no such row, the same
/// info-hiding shape every cross-tenant guard in this codebase already uses.</para>
/// </summary>
public sealed class DeleteVisitorContactDetailHandler(
    IConversationRepository conversations, IVisitorContactDetailRepository contactDetails, IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(DeleteVisitorContactDetail command, CancellationToken cancellationToken)
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

        var detail = await contactDetails.GetByIdAsync(command.ContactDetailId, cancellationToken);
        if (detail is null || detail.VisitorId != conversation.VisitorId)
        {
            return ConversationErrors.ContactDetailNotFound(command.ContactDetailId.Value);
        }

        await contactDetails.DeleteAsync(detail, cancellationToken);

        return Result.Success();
    }
}
