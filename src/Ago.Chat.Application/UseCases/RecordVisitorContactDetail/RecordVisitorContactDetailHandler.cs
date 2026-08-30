using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RecordVisitorContactDetail;

/// <summary>
/// `14-14`/`adr/0079` section 6.
///
/// <para><b>Gated on <see cref="Permission.ConversationSend"/>, the backlog item's own instruction,
/// for the identical reason <c>RequestChannelLinkFromConsoleHandler</c>'s own remarks already give for
/// reusing it: "recording a fact told to the operator inside a conversation is not more sensitive than
/// replying in it." No new, dedicated permission - unlike <see cref="Permission.ChannelIdentityUnlink"/>,
/// there is no routing capability being protected here, only a note; the backlog item's own Scope
/// section names this explicitly.</b></para>
///
/// <para>Tenant scope is checked the same way <c>RequestChannelLinkFromConsoleHandler</c> checks it -
/// <see cref="IConversationRepository.GetByIdAsync"/> (unscoped by site) plus an explicit
/// <c>conversation.SiteId != command.SiteId</c> comparison, not
/// <see cref="ListChannelIdentitiesForVisitor.ListChannelIdentitiesForVisitorHandler"/>'s narrower
/// assigned-operator check. That check exists there because unlinking/relinking a channel identity is
/// scoped to whoever currently owns the conversation; recording a fact a visitor just said, like
/// requesting a link code, is a site-wide capability every operator holding
/// <see cref="Permission.ConversationSend"/> already has for this conversation's own send path, so
/// narrowing it further here would add a restriction the backlog item never asked for.</para>
/// </summary>
public sealed class RecordVisitorContactDetailHandler(
    IConversationRepository conversations,
    IVisitorContactDetailRepository contactDetails,
    IPermissionChecker permissions,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<RecordedVisitorContactDetail>> HandleAsync(
        RecordVisitorContactDetail command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationSend, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to send messages in this conversation.");
        }

        if (!Enum.TryParse<VisitorContactDetailKind>(command.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
        {
            return ConversationErrors.ContactDetailInvalidKind(command.Kind);
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null || conversation.SiteId != command.SiteId)
        {
            // Wrong-tenant reads like no row - the same info-hiding shape every cross-tenant guard in
            // this codebase already uses (ConversationErrors.NotFound's own callers).
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        var now = clock.UtcNow;
        VisitorContactDetail detail;
        try
        {
            detail = VisitorContactDetail.Record(
                new VisitorContactDetailId(idGenerator.NewId(now)), conversation.VisitorId, kind, command.Value,
                command.RequestedBy, now);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.ContactDetailInvalid(ex.Message);
        }

        await contactDetails.SaveAsync(detail, cancellationToken);

        return new RecordedVisitorContactDetail(
            detail.Id.Value, detail.VisitorId.Value, detail.Kind.ToString(), detail.Value,
            detail.RecordedByOperatorId.Value, detail.RecordedAt);
    }
}
