using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ListVisitorContactDetails;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RevealVisitorContactDetail;

/// <summary>
/// `23-11`/`decisions.md` §5: "masked, revealed on demand, and the reveal is recorded." Gated on
/// <see cref="Permission.ConversationRead"/> - the identical permission and the identical tenant-scope
/// check <see cref="ListVisitorContactDetailsHandler"/> already applies, because a reveal is not a
/// second, stronger capability layered on top of reading a conversation; it is the same capability,
/// applied to one field the tenant's own setting chose to mask by default. There is deliberately no
/// check against <c>Site.ContactVisibility</c> here - see <see cref="RevealVisitorContactDetail"/>'s
/// own remarks for why the rung governs only the list read, never this one.
///
/// <para><b>Writes exactly one <c>contact_reveals</c> row, and only on success.</b> A caller refused by
/// the permission check or a conversation/detail that does not resolve never reaches the write - the
/// same "a denied or not-found read has nothing to attest to" principle <c>adr/0113</c>'s own remarks
/// state for <c>access_records</c>.</para>
///
/// <para><b>Wrong visitor reads like no such row</b> - the identical info-hiding shape
/// <see cref="DeleteVisitorContactDetail.DeleteVisitorContactDetailHandler"/>'s own remarks describe:
/// a contact detail id that exists but belongs to a different visitor than this conversation's own is
/// <see cref="ConversationErrors.ContactDetailNotFound"/>, not a different, more informative error.</para>
/// </summary>
public sealed class RevealVisitorContactDetailHandler(
    IConversationRepository conversations,
    IVisitorContactDetailRepository contactDetails,
    IPermissionChecker permissions,
    IContactRevealRepository reveals,
    IIdGenerator idGenerator,
    IClock clock)
{
    /// <summary>The one caller this item builds - the console's visitor-aside contact panel
    /// (`ui-inventory.md` §3.4, panel 7). A plain string, not an enum: unlike
    /// <see cref="Domain.AccessRecordActorKind"/>/<see cref="Domain.AccessRecordResourceKind"/> (a
    /// closed vocabulary both a writer and a reader must agree on), nothing yet reads this field back
    /// to branch on it - it exists so a future second surface (a report screen's own "reveal from
    /// here" action, say) does not silently conflate into an undifferentiated log, not to enumerate a
    /// closed set today.</summary>
    internal const string ConsoleContactPanelSurface = "ConsoleContactPanel";

    public async Task<Result<VisitorContactDetailDto>> HandleAsync(
        RevealVisitorContactDetail command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
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

        var now = clock.UtcNow;
        await reveals.RecordAsync(
            new ContactRevealToWrite(
                idGenerator.NewId(now), now, command.SiteId, command.ConversationId.Value, command.ContactDetailId.Value,
                command.RequestedBy, ConsoleContactPanelSurface),
            cancellationToken);

        return new VisitorContactDetailDto(
            detail.Id.Value, detail.Kind.ToString(), detail.Value, detail.RecordedByOperatorId?.Value,
            detail.Source.ToString(), detail.Verified, detail.RecordedAt, Masked: false);
    }
}
