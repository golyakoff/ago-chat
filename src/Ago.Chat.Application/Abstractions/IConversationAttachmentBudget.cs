using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-75`: a conversation's own byte budget - <c>AttachmentOptions.MaxConversationBytes</c>
/// (100 MiB by default), spent by a visitor and an operator alike, reserved at the moment a presign
/// slot is issued rather than counted as bytes actually land in storage. The split matters because of
/// `file-storage.md`'s own rule: bytes never pass through the API, so there is no later moment in the
/// request pipeline where an application-level counter could observe an upload actually happening -
/// the one write this codebase already controls, a `pending` row's own creation, is the only truthful
/// place to enforce a total.
///
/// <para><b>The same atomic compare-and-set shape as <see cref="IOperatorCapacity"/>, not
/// <see cref="IAttachmentRepository"/>.</b> Conceptually: reserve only if
/// <c>attachment_bytes_reserved + bytes &lt;= budget</c>, decided and written inside one Postgres
/// statement - CLAUDE.md rule 8: a compare-and-set read a write decision depends on comes from the
/// database inside the transaction, never a value read separately and trusted, which is exactly what
/// would let ten concurrent presign requests each see "still under budget" before any of them
/// commits. The real adapter's exact SQL is more careful than that one-line summary - see
/// <c>ConversationAttachmentBudgetStore</c>'s own remarks for why a bare
/// <c>UPDATE ... WHERE ... &lt;= @budget</c> is not quite enough to report the remaining bytes
/// correctly under real concurrency, which is this port's own contract for a refusal.</para>
///
/// <para><b>Reserved at presign, released by the existing pending sweep - not a new mechanism.</b>
/// `AttachmentOrphanSweepJob` (`5-04`) already deletes a `pending` row nobody confirmed within the
/// presign lifetime; `23-75` folds this reservation's release into that same atomic
/// `DELETE ... RETURNING` statement rather than inventing a second timeout or a second background job
/// (`file-storage.md`'s Upload flow, step 6, and this item's own design point). A confirmed (`Ready`)
/// attachment's reservation is never released on its own - the byte was actually spent, and stays
/// spent for the conversation's own lifetime, which `18-06`'s auto-close already bounds (this item's
/// own Scope: "active" means the conversation, so there is no second expiry to invent here).</para>
/// </summary>
public interface IConversationAttachmentBudget
{
    /// <summary>Attempts to reserve <paramref name="bytes"/> against <paramref name="budgetBytes"/>
    /// for <paramref name="conversationId"/>. <see cref="AttachmentBudgetResult.Reserved"/> is
    /// <see langword="true"/> only if the reservation actually happened;
    /// <see cref="AttachmentBudgetResult.RemainingBytes"/> is what is left of the budget either
    /// way - after the reservation on success, unchanged on refusal - read from the very statement
    /// that attempted the write, so a caller reporting "here is what you have left" to a refused
    /// request reports a real, un-raced number rather than a second query that could already be stale
    /// by the time it runs.</summary>
    Task<AttachmentBudgetResult> TryReserveAsync(
        ConversationId conversationId, long bytes, long budgetBytes, CancellationToken cancellationToken);

    /// <summary>Releases a previously-reserved amount. A no-op floor at zero, the identical shape
    /// <see cref="IOperatorCapacity.ReleaseAsync"/> already gives its own counter - a caller that
    /// raced a duplicate release cannot corrupt the total into negative territory.</summary>
    Task ReleaseAsync(ConversationId conversationId, long bytes, CancellationToken cancellationToken);
}

/// <summary>See <see cref="IConversationAttachmentBudget.TryReserveAsync"/>'s own remarks for why this
/// carries both fields back from one statement rather than a bare <see cref="bool"/>.</summary>
public readonly record struct AttachmentBudgetResult(bool Reserved, long RemainingBytes);
