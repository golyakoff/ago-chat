using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ListVisitorContactDetails;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.EditVisitorContactDetail;

/// <summary>
/// `25-58`: the write half of "real inline editing, not a second parallel form."
///
/// <para><b>Gated on <see cref="Permission.ConversationSend"/>, the identical permission and reasoning
/// <see cref="DeleteVisitorContactDetail.DeleteVisitorContactDetailHandler"/>'s own remarks already give:
/// there is no separate "edit"-shaped permission here, only the same operator population that may
/// record a contact detail also correcting one.</b></para>
///
/// <para><b>Tenant/visitor scope checked exactly the way <see cref="DeleteVisitorContactDetail.DeleteVisitorContactDetailHandler"/>
/// checks it</b> - resolve the conversation first (tenant-checked against <see cref="Domain.SiteId"/>),
/// then require the loaded detail's own <see cref="VisitorContactDetail.VisitorId"/> to match
/// <see cref="Conversation.VisitorId"/> before editing anything. A detail belonging to a different
/// visitor reads exactly like no such row - the same info-hiding shape every cross-tenant guard in this
/// codebase already uses.</para>
///
/// <para><b>Never touches <see cref="VisitorContactDetail.Source"/> or
/// <see cref="VisitorContactDetail.RecordedByOperatorId"/>.</b> This handler loads the existing row and
/// calls <see cref="VisitorContactDetail.EditValue"/> on it - it does not, and structurally cannot,
/// reassign who originally supplied the fact. See that method's own remarks for why this is the backlog
/// item's own explicit warning, not an incidental property of the implementation.</para>
///
/// <para>No outbox event on a successful edit, unlike <c>RecordVisitorContactDetailHandler</c>'s
/// <c>ContactCollected</c>. <c>ContactCollected</c>'s own remarks describe a
/// <see cref="VisitorContactDetail"/> as "written once and never edited," and key a far-side consumer's
/// idempotency and non-merge behaviour on <see cref="VisitorContactDetail.Id"/> staying a stable pointer
/// to one unchanging fact. Republishing on every edit would hand a future consumer of that event a
/// value that silently changed under an id it was told never would - a real, open question for whoever
/// builds that consumer, not one this item resolves unilaterally by guessing at a republish shape
/// nothing downstream yet exists to receive.</para>
/// </summary>
public sealed class EditVisitorContactDetailHandler(
    IConversationRepository conversations, IVisitorContactDetailRepository contactDetails, IPermissionChecker permissions)
{
    public async Task<Result<VisitorContactDetailDto>> HandleAsync(
        EditVisitorContactDetail command, CancellationToken cancellationToken)
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

        try
        {
            detail.EditValue(command.Value);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ContactDetailInvalid(ex.Message);
        }

        await contactDetails.SaveAsync(detail, cancellationToken);

        return new VisitorContactDetailDto(
            detail.Id.Value, detail.Kind.ToString(), detail.Value, detail.RecordedByOperatorId?.Value,
            detail.Source.ToString(), detail.Verified, detail.RecordedAt, Masked: false, detail.Assessment.ToString());
    }
}
