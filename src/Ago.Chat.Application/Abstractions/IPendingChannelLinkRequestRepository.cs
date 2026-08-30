using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-12`: the write side of <see cref="PendingChannelLinkRequest"/> - shaped by its two real callers,
/// never a generic <c>IRepository&lt;T&gt;</c> (clean-architecture.md).
///
/// <para><b>Two save methods, deliberately, unlike every other repository in this codebase.</b> This
/// aggregate is created from two structurally different kinds of caller (`adr/0079` decision 2):
/// <see cref="SaveAsync"/> is for an ordinary, standalone use case
/// (<c>RequestChannelLinkFromConsoleHandler</c>) that owns its own transaction and wants this write
/// committed immediately, the same shape <c>IOperatorInviteRepository.SaveAsync</c> already has.
/// <see cref="Stage"/> is for the `MessageAccepted`-driven visitor-initiated path
/// (<c>HandleLinkIdentityCommandHandler</c>), which must not commit anything of its own - every side
/// effect that handler produces (this row, the reply message, the outbox row) has to land in the single
/// transaction <see cref="IInboxChecker.TryRecordAndSaveAsync"/> performs, or a redelivery before that
/// point would mint a second, orphaned pending request every time (`adr/0017`'s own same-transaction
/// guarantee, and the exact mistake <see cref="IOutboxWriter"/>'s own remarks warn a caller against).
/// <see cref="Stage"/> mirrors <see cref="IOutboxWriter.Enqueue"/>'s own shape for the identical
/// reason: synchronous, no I/O, adds to whichever <c>DbContext</c> is already tracking the caller's
/// unit of work.</para>
/// </summary>
public interface IPendingChannelLinkRequestRepository
{
    /// <summary>
    /// The one live request matching this exact (site, channel kind, code) triple, or
    /// <see langword="null"/> if none exists, has already been consumed, or has expired. Scoped to
    /// (site, kind) as well as the code hash, never the hash alone - <see cref="PendingChannelLinkRequest"/>'s
    /// own remarks on why a coincidental cross-site code collision must never match.
    /// </summary>
    Task<PendingChannelLinkRequest?> FindLiveAsync(
        SiteId siteId, ChannelKind kind, byte[] codeHash, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Adds and immediately commits - the standalone-use-case path. See this interface's own
    /// remarks for why <see cref="Stage"/> exists as a separate method rather than this one being reused
    /// for both callers.</summary>
    Task SaveAsync(PendingChannelLinkRequest request, CancellationToken cancellationToken);

    /// <summary>Adds to the tracked context without saving - the `MessageAccepted`-consumer path. See
    /// this interface's own remarks.</summary>
    void Stage(PendingChannelLinkRequest request);
}
