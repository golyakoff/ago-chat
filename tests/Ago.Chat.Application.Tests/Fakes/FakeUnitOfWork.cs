using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>
/// `18-02`: an in-memory <see cref="IUnitOfWork"/> that records whether a transaction was committed -
/// real atomicity (claim/release/save rolling back together) is a claim about Postgres, and
/// testing.md puts it where it can actually be proven (<c>TransferConversationConcurrencyTests</c>,
/// <c>Ago.Chat.Integration.Tests</c>). What a handler unit test can prove is the decision: whether the
/// handler asked for a commit at all, for a given attempt.
/// </summary>
public sealed class FakeUnitOfWork : IUnitOfWork
{
    public int TransactionsBegun { get; private set; }

    public int TransactionsCommitted { get; private set; }

    public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        TransactionsBegun++;
        return Task.FromResult<IUnitOfWorkTransaction>(new FakeTransaction(this));
    }

    private sealed class FakeTransaction(FakeUnitOfWork owner) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            owner.TransactionsCommitted++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
