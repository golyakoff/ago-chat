using Ago.Chat.Application.Abstractions;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `18-02`'s <see cref="IUnitOfWork"/>, the one place this codebase opens an explicit
/// <c>db.Database.BeginTransactionAsync</c> for a per-request handler rather than relying on one
/// aggregate's own implicit <c>SaveChangesAsync</c> transaction. See the port's own remarks for why a
/// handler cannot do this directly (CLAUDE.md rule 2: no <c>DbContext</c> above Infrastructure).
///
/// Scoped, like every other adapter over <see cref="AgoChatDbContext"/> in this project
/// (<c>ServiceCollectionExtensions</c>) - the same scoped <c>db</c> instance this transaction begins
/// on is also the instance <c>ConversationRepository</c> and <c>OperatorCapacityStore</c> were
/// constructed with for the same request, which is what makes their statements land inside this
/// transaction with no further wiring: EF's <c>ExecuteSqlAsync</c> family and <c>SaveChangesAsync</c>
/// both participate in <c>Database.CurrentTransaction</c> automatically once it is set.
/// </summary>
public sealed class EfUnitOfWork(AgoChatDbContext db) : IUnitOfWork
{
    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        return new EfUnitOfWorkTransaction(transaction);
    }

    private sealed class EfUnitOfWorkTransaction(IDbContextTransaction transaction) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => transaction.CommitAsync(cancellationToken);

        // No explicit RollbackAsync call: IDbContextTransaction.DisposeAsync rolls back on its own
        // when CommitAsync was never called - see IUnitOfWork's own remarks for why that is the whole
        // contract rather than a second method to remember to call.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
