using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner;

/// <summary>
/// `23-85`/`adr/0151`: the second, deliberately separate step of the walkthrough
/// <see cref="ListNonEntitledChannelCredentialsAsOwner.ListNonEntitledChannelCredentialsAsOwner"/>'s
/// own remarks describe - never a single "disconnect everyone unentitled" call with no explicit list.
///
/// <para><b>Why explicit ids, not "find and fix everything again."</b> The backlog item's own hard
/// requirement is that this "is not authorised to switch on silently: the author wants to walk through
/// the whole flow end to end, personally, before it runs for real." A parameterless "reconcile
/// everything now" command could be triggered once, by habit, without anyone having actually looked at
/// what it was about to do. Requiring the caller to name the exact <see cref="ChannelCredentialId"/>s
/// makes the review the list query above exists for a precondition of the act, not an optional
/// courtesy - the same reasoning <c>GrantModuleQuantity</c>'s own <c>ExpectedAffectedCount</c> preview-
/// then-confirm shape already applies to a different bulk write in this codebase.</para>
///
/// <para><b>Re-verified at execution time, not trusted from the list.</b> See
/// <see cref="DisconnectNonEntitledChannelCredentialsAsOwnerHandler"/>'s own remarks - a credential the
/// owner selected from a list read moments (or minutes) earlier may have been paid for, or revoked by
/// its own tenant, in between; this command re-checks rather than blindly acting on stale input.</para>
/// </summary>
public sealed record DisconnectNonEntitledChannelCredentialsAsOwner(
    IReadOnlyList<ChannelCredentialId> ChannelCredentialIds);

/// <summary>One <see cref="ChannelCredentialId"/> from the request, and what actually happened to it -
/// never a bare success/failure for the whole batch, because a caller reviewing the outcome needs to
/// know which of several possible reasons applied to which row.</summary>
public sealed record ChannelCredentialDisconnectOutcome(ChannelCredentialId ChannelCredentialId, ChannelCredentialDisconnectStatus Status);

public enum ChannelCredentialDisconnectStatus
{
    /// <summary>Was active and unentitled; is now revoked and its stored token(s) cleared - see
    /// <see cref="Domain.ChannelCredential.RevokeForLapsedEntitlement"/>.</summary>
    Disconnected,

    /// <summary>No credential exists with this id - a stale list, or a typo in the request.</summary>
    NotFound,

    /// <summary>The credential was already inactive (revoked by its own tenant, or by an earlier call
    /// to this same command) - a no-op, not an error, the identical idempotent-retry shape
    /// <c>RevokeChannelCredentialHandler</c>'s own remarks state for itself.</summary>
    AlreadyInactive,

    /// <summary>The account holds an entitlement for this credential's <see cref="ChannelKind"/> after
    /// all, re-checked at the moment this command ran rather than trusted from whatever list produced
    /// this id - the account paid (or was granted) in the gap between the owner reading the list and
    /// acting on it, and this row is left connected.</summary>
    StillEntitled,
}
