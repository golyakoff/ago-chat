using Ago.Chat.Application.Abstractions;
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
/// </summary>
public sealed class ListVisitorContactDetailsHandler(
    IConversationRepository conversations, IVisitorContactDetailRepository contactDetails, IPermissionChecker permissions)
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

        var items = await contactDetails.GetForVisitorAsync(conversation.VisitorId, cancellationToken);
        IReadOnlyList<VisitorContactDetailDto> dtos = items
            .Select(d => new VisitorContactDetailDto(
                d.Id.Value, d.Kind.ToString(), d.Value, d.RecordedByOperatorId.Value, d.RecordedAt))
            .ToList();

        return Result<IReadOnlyList<VisitorContactDetailDto>>.Success(dtos);
    }
}
