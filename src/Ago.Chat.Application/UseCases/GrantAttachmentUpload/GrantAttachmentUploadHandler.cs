using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GrantAttachmentUpload;

/// <summary>
/// `23-78`: the operator's own side of the control - "an operator ticks «разрешаю пользователю
/// отправлять файлы», and without it there is no upload control at all" (the backlog item's own
/// Decision). Gated by <see cref="Permission.ConversationAttachmentUploadGrant"/> - see that field's
/// own remarks for why this is Operator-scoped, not Admin-scoped like
/// <see cref="Application.UseCases.BlockConversation.BlockConversationHandler"/>.
///
/// <para><b>Loads the aggregate for the assignment check, writes through the raw-SQL repository.</b>
/// Unlike <see cref="Application.UseCases.BlockConversation.BlockConversationHandler"/> (which never
/// loads <see cref="Conversation"/> at all - blocking is permission-only, any operator holding
/// <see cref="Permission.ConversationBlock"/> may act on any conversation on the site), granting an
/// upload is scoped to "the operator handling this conversation" - the identical "RBAC answers may this
/// operator act at all, a per-conversation comparison answers on this one" split
/// <c>CloseConversationHandler</c>/<c>CreateAttachmentHandler.HandleAsOperatorAsync</c> already draw for
/// <see cref="Permission.ConversationSend"/>. That comparison needs <see cref="Conversation.OperatorId"/>,
/// which only a real load can answer - so this handler pays for one <see cref="IConversationRepository.GetByIdAsync"/>
/// read. The actual write is <see cref="IConversationAttachmentUploadGrantRepository.GrantAsync"/>, the
/// same raw-SQL bypass <see cref="Application.UseCases.BlockConversation.BlockConversationHandler"/> uses
/// for the identical xmin-racing reason (<see cref="IConversationAttachmentUploadGrantRepository"/>'s own
/// remarks).</para>
///
/// <para><b>`25-110`: now opens an <see cref="IUnitOfWork"/> transaction and calls
/// <see cref="IConversationRepository.SaveAsync"/> after all - but only to flush the outbox row this
/// handler now stages, never to save a mutation.</b> Before this item, neither
/// <see cref="GrantAsync"/> nor a revoke enqueued any integration event at all, so a visitor whose widget
/// connection was already open never learned the grant changed until an unrelated reconnect rode past it
/// (`ago-widget`'s own `connection.ts` doc comment on `onAttachmentUploadGrantChange` used to say so in
/// as many words - this item is what makes that doc comment stop being true). Fixing that means enqueuing
/// through <see cref="IOutboxWriter"/> - CLAUDE.md rule 4 forbids publishing directly from a request
/// handler - and the enqueue has to commit in the same Postgres transaction as
/// <see cref="GrantAsync"/>'s own write, or a crash between the two could persist a grant with no event
/// behind it. <see cref="IConversationAttachmentUploadGrantRepository"/>'s own adapter now issues its SQL
/// through the same <c>AgoChatDbContext</c> connection <see cref="IConversationRepository"/> and
/// <see cref="IUnitOfWork"/> already share (<c>ConversationAttachmentBudgetStore</c>'s own precedent for
/// "raw SQL through the ambient connection, not a second <c>NpgsqlDataSource</c>"), so a transaction begun
/// here really does cover both statements. <see cref="IOutboxWriter.Enqueue"/> only stages the row on that
/// same <c>DbContext</c>'s change tracker (<c>EfOutboxWriter</c>'s own shape, the identical mechanism
/// every other handler in this codebase relies on) - nothing flushes it to Postgres until a
/// <c>SaveChangesAsync</c> actually runs, which is exactly what
/// <see cref="IConversationRepository.SaveAsync"/> does. Calling it here does **not** reintroduce the
/// load-mutate-save race this handler's own history avoided: <paramref name="conversation"/> above is
/// never mutated (nothing here calls a method on it), so it stays <c>Unchanged</c> in the tracker and
/// <c>SaveChangesAsync</c> emits no <c>UPDATE</c> for it at all - no `xmin` comparison, no concurrency
/// exception possible, only the newly-staged outbox row actually gets written. This is the identical
/// "the row I loaded stays untouched, only something else on this same context needs flushing" shape
/// <c>TransferConversationHandler</c> uses `IConversationRepository.SaveAsync` for, restated for a handler
/// that saves nothing about the aggregate itself.</para>
/// </summary>
public sealed class GrantAttachmentUploadHandler(
    IConversationRepository conversations,
    IConversationAttachmentUploadGrantRepository grants,
    IPermissionChecker permissions,
    IUnitOfWork unitOfWork,
    IOutboxWriter outbox,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<AttachmentUploadGrantStatus>> HandleAsync(
        GrantAttachmentUpload command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationAttachmentUploadGrant, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to grant attachment uploads for this site.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        if (conversation.OperatorId != command.RequestedBy)
        {
            return ConversationErrors.Forbidden("This operator is not assigned to this conversation.");
        }

        var now = clock.UtcNow;

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var outcome = await grants.GrantAsync(command.ConversationId, command.SiteId, command.RequestedBy, now, cancellationToken);

        if (outcome == AttachmentUploadGrantOutcome.Applied)
        {
            // `25-110`: enqueued only on an actual transition - "not on AlreadyInState, nothing changed,
            // nothing to tell anyone" (the item's own Scope). conversation.VisitorId, not a second load:
            // this handler already paid for the one GetByIdAsync above.
            outbox.Enqueue(AttachmentUploadGrantChangedMapper.ToEnvelope(
                command.ConversationId, conversation.VisitorId, granted: true, now, idGenerator));

            // Flushes the outbox row staged above, and only that - see this type's own remarks for why
            // this cannot reintroduce the xmin race this handler was built to avoid.
            await conversations.SaveAsync(conversation, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // NotFound/AlreadyInState: the transaction is disposed without a commit right below, rolling
        // back grants.GrantAsync's own conditional UPDATE - which touched zero rows in either case, so
        // there is nothing for that rollback to actually undo.
        return outcome switch
        {
            AttachmentUploadGrantOutcome.NotFound => ConversationErrors.NotFound(command.ConversationId.Value),
            AttachmentUploadGrantOutcome.AlreadyInState =>
                ConversationErrors.ConversationAttachmentUploadAlreadyGranted(command.ConversationId.Value),
            AttachmentUploadGrantOutcome.Applied => new AttachmentUploadGrantStatus(command.ConversationId, now, command.RequestedBy),
            _ => throw new InvalidOperationException($"Unhandled {nameof(AttachmentUploadGrantOutcome)}: {outcome}."),
        };
    }
}

/// <summary>The grant/revoke actions' own success response - both
/// <see cref="GrantAttachmentUploadHandler"/> and
/// <see cref="Application.UseCases.RevokeAttachmentUpload.RevokeAttachmentUploadHandler"/> return this,
/// since both answer the identical question ("what is this conversation's attachment-upload grant
/// state now") for opposite directions - the same reuse
/// <c>BlockConversationHandler</c>/<c>UnblockConversationHandler</c>'s own shared
/// <c>ConversationBlockStatus</c> already establishes.</summary>
public sealed record AttachmentUploadGrantStatus(ConversationId ConversationId, DateTimeOffset OccurredAt, OperatorId OperatorId);
