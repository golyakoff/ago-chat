using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `14-15`: the write side of <see cref="PendingPhoneVerification"/> - shaped by its two real callers,
/// never a generic <c>IRepository&lt;T&gt;</c> (clean-architecture.md).
///
/// <para><b>One <see cref="SaveAsync"/>, unlike <see cref="IPendingChannelLinkRequestRepository"/>'s own
/// two.</b> That port needs a second, non-committing <c>Stage</c> method because one of its two callers
/// (the `MessageAccepted`-driven visitor-initiated path) must not commit anything of its own - every side
/// effect has to land inside a different aggregate's own transaction. Both of this type's callers
/// (<c>InitiatePhoneVerificationHandler</c>, <c>ConfirmPhoneVerificationHandler</c>) are ordinary,
/// standalone use cases that own their own request and want this write committed immediately - the same
/// single-<c>SaveAsync</c> shape <see cref="IOperatorRepository"/> already has.</para>
/// </summary>
public interface IPendingPhoneVerificationRepository
{
    /// <summary>By primary key - the only lookup this item needs. Unfiltered by expiry/consumption/lockout,
    /// deliberately: <c>ConfirmPhoneVerificationHandler</c> needs to see the row's real current state to
    /// report which of <see cref="PhoneVerificationConfirmOutcome"/>'s members applies, which an
    /// <c>IPendingChannelLinkRequestRepository.FindLiveAsync</c>-shaped "only ever returns a live row"
    /// query would hide.</summary>
    Task<PendingPhoneVerification?> GetByIdAsync(PendingPhoneVerificationId id, CancellationToken cancellationToken);

    /// <summary>Adds a new row, or persists a mutation (<see cref="PendingPhoneVerification.AttemptConfirm"/>'s
    /// own <c>AttemptCount</c>/<c>ConsumedAt</c> change) to one already tracked - the same "insert if new,
    /// update if not" shape <c>PendingChannelLinkRequestRepository.Stage</c>'s own EF-state check
    /// establishes, folded into one method here because there is only ever one commit point per
    /// call.</summary>
    Task SaveAsync(PendingPhoneVerification verification, CancellationToken cancellationToken);
}
