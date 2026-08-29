namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `18-02`: the one port this item adds to make "claim on one operator, release on another, the
/// conversation's own state change, and its outbox row - one Postgres transaction, or none of them"
/// (the backlog item's own Scope) expressible from <c>Ago.Chat.Application</c> at all.
///
/// <para><b>Why this exists rather than a handler calling <c>AgoChatDbContext.Database.
/// BeginTransactionAsync</c> directly.</b> CLAUDE.md rule 2: no <c>DbContext</c>, no Npgsql, above
/// Infrastructure. Every other write in this codebase gets its atomicity for free from one
/// <c>SaveChangesAsync</c> (<see cref="IConversationRepository.SaveAsync"/>'s own implicit
/// transaction) because it touches exactly one aggregate - <c>CloseConversationHandler</c>'s own
/// remarks are explicit that its capacity release is deliberately <em>not</em> part of that
/// transaction. A transfer is the first per-request handler in this codebase that genuinely needs
/// more than one write - two raw <see cref="IOperatorCapacity"/> statements plus the conversation's
/// own save - to commit or roll back as a single unit, and no existing port gives it that.
/// <c>Ago.Chat.Worker</c>'s <c>SkipLockedAssignmentClaimer</c> does the equivalent for its own
/// multi-conversation batch, but it is a host, not an <c>Application</c> handler, and it is allowed to
/// construct <see cref="Ago.Chat.Infrastructure.Postgres.Persistence.AgoChatDbContext"/> and an
/// <c>NpgsqlDataSource</c> directly for exactly that reason - a per-request handler is not a host and
/// must not gain that same freedom just because one use case now needs a transaction.</para>
///
/// <para><b>The alternative that was not taken</b>: folding "claim, release" into a single new
/// <see cref="IOperatorCapacity"/> method (a hypothetical <c>MoveAsync(from, to, ct)</c>) would avoid
/// a new port, but it would still need to run in the same transaction as
/// <see cref="IConversationRepository.SaveAsync"/> and the outbox write - two other ports this
/// handler also calls - so the transaction boundary has to be visible to the handler regardless of
/// how many statements <c>IOperatorCapacity</c> itself exposes. A capacity-only port cannot express
/// "and also commit the conversation's own save with it" no matter how it is shaped; only something
/// that wraps the whole request's unit of work can.</para>
///
/// Deliberately minimal: no <c>RollbackAsync</c>. Every adapter's own transaction type
/// (<c>Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction</c>) already rolls back on
/// <see cref="IAsyncDisposable.DisposeAsync"/> if <see cref="IUnitOfWorkTransaction.CommitAsync"/> was
/// never called - the same "dispose without commit means abort" shape a plain
/// <c>await using var transaction = ...</c> block already gives every ADO.NET/EF caller, so a second,
/// explicit method to spell the same thing would only be one more way to forget to call it.
/// </summary>
public interface IUnitOfWork
{
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
}

/// <summary>See <see cref="IUnitOfWork"/>'s own remarks for why this has no <c>RollbackAsync</c>.</summary>
public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
