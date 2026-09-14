using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>`23-80`'s own Done-when: "a tenant sees how much of their quota is used, and it agrees
/// with what enforcement believes." <c>ISiteAttachmentStorageBudget</c> (`23-76`) is the enforcement
/// port itself, write-shaped (<c>TryReserveAsync</c>/<c>ReleaseAsync</c>) with no plain read - and it
/// is left untouched here rather than widened, per this item's own brief ("`23-76` is already done -
/// do not touch its own scope"). This is a second, tiny, additive port instead: a bare read of the
/// exact same column (<c>sites.attachment_bytes_reserved</c>) that store's own upsert already
/// maintains, so "the number agrees" is true by construction - both read the one column, neither
/// recomputes it by summing attachment rows (the backlog item's own explicit warning: "counting is not
/// free... needs to come from something maintained, not from summing rows on every page load").</summary>
public interface IAttachmentBudgetReadStore
{
    /// <summary>Zero, never a missing value, for a site with no reservation yet - the same
    /// "no evidence and zero are the same fact" reasoning <see cref="IAttachmentEgressReadStore"/>
    /// applies.</summary>
    Task<long> GetReservedBytesAsync(SiteId siteId, CancellationToken cancellationToken);
}
