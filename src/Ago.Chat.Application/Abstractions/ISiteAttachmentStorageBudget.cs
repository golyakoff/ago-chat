using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `23-76`: the tenant's own byte budget - the sibling `23-75`'s <see cref="IConversationAttachmentBudget"/>
/// never had. That port bounds one conversation's own total; nothing before this item bounded the
/// *site's* total, and a visitor token is free to mint, so a per-conversation ceiling alone "does not
/// protect storage at all" (this item's own opening words) - an attacker who exhausts one
/// conversation's 100 MiB simply opens another and gets a fresh one. What bounds storage is a ceiling
/// on the thing an attacker cannot mint for free: the tenant.
///
/// <para><b>The identical shape as <see cref="IConversationAttachmentBudget"/>, keyed by
/// <see cref="SiteId"/> instead of <see cref="ConversationId"/>, reusing its own
/// <see cref="AttachmentBudgetResult"/></b> rather than a second, parallel record type - the contract
/// ("reserved iff it fits, remaining reported either way, read from the same statement that attempted
/// the write") is exactly the same fact about a different denormalized running total, so a second type
/// would only be a second name for the same shape. See that port's own remarks for the full reasoning
/// (CLAUDE.md rule 8: a compare-and-set read a write decision depends on comes from the database
/// inside the transaction, never a separately-read value).</para>
///
/// <para><b>Reserved at presign, alongside the conversation's own reservation - both must succeed or
/// neither does.</b> <c>CreateAttachmentHandler.CreateAsync</c> calls this in the same transaction as
/// <see cref="IConversationAttachmentBudget.TryReserveAsync"/>, right before the presigned URL is ever
/// issued - a request that would blow the tenant's total quota is refused before storage ever hands
/// out a slot for it, the same "the write decision and the reservation happen together, or roll back
/// together" property that port's own conversation-level reservation already has.</para>
///
/// <para><b>Released on the identical paths <see cref="IConversationAttachmentBudget"/> already
/// releases on</b> - a rejected upload never reserves in the first place (both ports refuse and the
/// transaction rolls back), an abandoned `pending` attachment is released by the same orphan sweep
/// that already releases the conversation's own reservation (folded into the identical atomic
/// statement, `AttachmentOrphanSweepQuery`), and a confirmed attachment whose bytes turn out to
/// duplicate an existing object within the same tenant (`23-76`'s own dedup mechanism) releases this
/// reservation specifically, without touching the conversation's own - the tenant's disk cost dropped
/// to zero net new bytes, but the conversation's own "how much have I asked for" total is unaffected
/// (a UX/sanity cap, not a storage-economics one). A confirmed, non-duplicate attachment is never
/// released on its own - the byte was actually spent.</para>
/// </summary>
public interface ISiteAttachmentStorageBudget
{
    /// <summary>Attempts to reserve <paramref name="bytes"/> against <paramref name="budgetBytes"/>
    /// for <paramref name="siteId"/>. See <see cref="IConversationAttachmentBudget.TryReserveAsync"/>'s
    /// own remarks for the exact contract - identical here, just keyed by tenant rather than
    /// conversation.</summary>
    Task<AttachmentBudgetResult> TryReserveAsync(
        SiteId siteId, long bytes, long budgetBytes, CancellationToken cancellationToken);

    /// <summary>Releases a previously-reserved amount. A no-op floor at zero, the identical shape
    /// <see cref="IConversationAttachmentBudget.ReleaseAsync"/> already gives its own counter.</summary>
    Task ReleaseAsync(SiteId siteId, long bytes, CancellationToken cancellationToken);
}
