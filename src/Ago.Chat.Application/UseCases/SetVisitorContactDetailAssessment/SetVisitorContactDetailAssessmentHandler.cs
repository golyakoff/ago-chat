using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ListVisitorContactDetails;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SetVisitorContactDetailAssessment;

/// <summary>
/// `25-58`: the write half of the confirm/mark-invalid action - Phone and Email only, gated exactly the
/// way <see cref="EditVisitorContactDetail.EditVisitorContactDetailHandler"/> and
/// <see cref="DeleteVisitorContactDetail.DeleteVisitorContactDetailHandler"/> already are
/// (<see cref="Permission.ConversationSend"/>, resolved through the conversation, wrong-visitor reads
/// like no such row).
///
/// <para><b>Rejects <see cref="VisitorContactDetailKind.Other"/> here, before ever calling
/// <see cref="VisitorContactDetail.SetAssessment"/>.</b> The console never offers this action for an
/// `Other` row, so reaching this handler with one at all means a different, malformed request - this is
/// the ordinary, expected case the Application layer resolves with a normal
/// <see cref="ConversationErrors.ContactDetailAssessmentNotApplicable"/>, not the domain method's own
/// defence-in-depth <see cref="InvalidVisitorContactDetailStateException"/>
/// (<see cref="InvalidVisitorContactDetailStateException"/>'s own remarks on the split).</para>
///
/// <para><b>Refuses <see cref="VisitorContactDetailAssessment.Unset"/> as a settable target</b> - the
/// identical "not itself a settable target" rule <see cref="SetConversationOutcome.SetConversationOutcomeHandler"/>
/// already enforces for <see cref="ConversationOutcome.Unset"/>. An operator who wants to take back a
/// confirmed/invalid call has no path back to "nothing asserted" through this handler today - the
/// backlog item names only "confirmed or invalid," never a reversal, so this follows the identical
/// precedent rather than inventing a reversal nothing asked for.</para>
/// </summary>
public sealed class SetVisitorContactDetailAssessmentHandler(
    IConversationRepository conversations, IVisitorContactDetailRepository contactDetails, IPermissionChecker permissions)
{
    public async Task<Result<VisitorContactDetailDto>> HandleAsync(
        SetVisitorContactDetailAssessment command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages in this conversation.");
        }

        if (!Enum.TryParse<VisitorContactDetailAssessment>(command.Assessment, ignoreCase: true, out var assessment)
            || !Enum.IsDefined(assessment)
            || assessment == VisitorContactDetailAssessment.Unset)
        {
            return ConversationErrors.ContactDetailInvalidAssessment(command.Assessment);
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

        if (detail.Kind == VisitorContactDetailKind.Other)
        {
            return ConversationErrors.ContactDetailAssessmentNotApplicable(detail.Kind.ToString());
        }

        detail.SetAssessment(assessment);
        await contactDetails.SaveAsync(detail, cancellationToken);

        return new VisitorContactDetailDto(
            detail.Id.Value, detail.Kind.ToString(), detail.Value, detail.RecordedByOperatorId?.Value,
            detail.Source.ToString(), detail.Verified, detail.RecordedAt, Masked: false, detail.Assessment.ToString());
    }
}
