using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.UseCases.DisconnectNonEntitledChannelCredentialsAsOwner;

/// <summary>
/// `23-85`/`adr/0151`: the write half of the owner's own walkthrough - see
/// <see cref="DisconnectNonEntitledChannelCredentialsAsOwner"/>'s own remarks for why this takes an
/// explicit list of ids rather than reconciling everything by itself, and
/// <see cref="Domain.ChannelCredential.RevokeForLapsedEntitlement"/>'s own remarks for what
/// "disconnected and cleaned up" means at the row level.
///
/// <para><b>No new attribution/reason machinery, unlike `adr/0118`'s forced-revoke-of-a-purchase
/// path.</b> That ADR built `Force`/`Reason`/a dedicated `module_revoke_overrides` table because it
/// overrides a tenant's own paid-for grant against their will - the exact case this item is not: an
/// account with no entitlement never had a right to the connection in the first place, so there is no
/// "against their will" to attest to. `23-86`'s own "Answered" section already settled the identical
/// question for the sibling billing-lapse case: a system-triggered revoke is self-documenting ("the
/// subscription lapsed" is itself the reason) and needs no new table. This handler is the same
/// judgement applied to the same kind of act: unlike `SuspendTenantAsOwner`/`ExtendSuspensionAsOwner`
/// (each given a free-text `Reason` because a suspension's own remedy is genuinely ambiguous without
/// one), "this account has no channel entitlement" already is the whole reason, spelled out in
/// <see cref="ChannelCredentialDisconnectStatus"/> and returned to the caller per credential - there is
/// no second, human-authored justification for this route to carry.</para>
/// </summary>
public sealed class DisconnectNonEntitledChannelCredentialsAsOwnerHandler(
    IChannelCredentialRepository credentials, IBillingOptionEntitlementProvider entitlements,
    IModuleQuantityGrantStore grants)
{
    public async Task<IReadOnlyList<ChannelCredentialDisconnectOutcome>> HandleAsync(
        DisconnectNonEntitledChannelCredentialsAsOwner command, CancellationToken cancellationToken)
    {
        var outcomes = new List<ChannelCredentialDisconnectOutcome>(command.ChannelCredentialIds.Count);

        foreach (var id in command.ChannelCredentialIds)
        {
            var credential = await credentials.GetByIdAsync(id, cancellationToken);
            if (credential is null)
            {
                outcomes.Add(new ChannelCredentialDisconnectOutcome(id, ChannelCredentialDisconnectStatus.NotFound));
                continue;
            }

            if (!credential.Active)
            {
                outcomes.Add(new ChannelCredentialDisconnectOutcome(id, ChannelCredentialDisconnectStatus.AlreadyInactive));
                continue;
            }

            // Re-checked here, not trusted from whichever list produced this id - see this type's own
            // remarks on why a stale selection must not blindly disconnect an account that has since
            // become entitled.
            var entitled = await ChannelEntitlement.IsEntitledAsync(
                entitlements, grants, credential.SiteId, credential.Kind, cancellationToken);
            if (entitled)
            {
                outcomes.Add(new ChannelCredentialDisconnectOutcome(id, ChannelCredentialDisconnectStatus.StillEntitled));
                continue;
            }

            credential.RevokeForLapsedEntitlement();
            await credentials.SaveAsync(credential, cancellationToken);
            outcomes.Add(new ChannelCredentialDisconnectOutcome(id, ChannelCredentialDisconnectStatus.Disconnected));
        }

        return outcomes;
    }
}
