using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RevokeChannelCredential;

/// <summary>
/// `14-02`/`adr/0069`: flips `Active` to `false`, never a hard delete - `RevokeWebhookEndpointHandler`'s
/// own shape, including the same idempotent short-circuit for a retried/double-clicked request. Erasing
/// the row entirely is a separate, later concern (`16-02`, tenant offboarding) - see
/// `Domain.ChannelCredential.Revoke`'s own remarks.
///
/// <para><b>`23-85`/`adr/0151`: this path checks the channel entitlement too, per the backlog item's
/// own literal Scope ("connecting, rotating or revoking a channel credential requires a channel
/// entitlement") - and that reading is worth naming as a tension rather than applying silently.</b>
/// The other two motivations for the gate (stop someone unentitled from starting to use a channel;
/// stop someone unentitled from re-pointing an already-working one at a new token) do not obviously
/// apply to *tearing a connection down* - if anything, a tenant whose entitlement just lapsed asking to
/// disconnect their own now-useless credential looks like exactly the self-service act this codebase
/// should always allow, entitled or not. This handler implements the item's literal text rather than
/// resolving that tension unilaterally; see this item's own worker report for the question surfaced
/// back to the author. In the meantime, the platform's own lapsed-entitlement cleanup (the
/// owner-triggered disconnect use case this item also ships) does not route through this handler at
/// all - it calls <see cref="Domain.ChannelCredential.RevokeForLapsedEntitlement"/> directly, so a
/// system-triggered disconnect is never blocked by the very check this handler now performs.</para>
/// </summary>
public sealed class RevokeChannelCredentialHandler(
    IChannelCredentialRepository credentials, IPermissionChecker permissions,
    IBillingOptionEntitlementProvider entitlements, IModuleQuantityGrantStore grants)
{
    public async Task<Result> HandleAsync(RevokeChannelCredential command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Domain.Permission.ChannelManage, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage channels for this site.");
        }

        var credential = await credentials.GetByIdAsync(command.ChannelCredentialId, cancellationToken);
        if (credential is null || credential.SiteId != command.SiteId)
        {
            return ConversationErrors.ChannelCredentialNotFound(command.ChannelCredentialId.Value);
        }

        if (!credential.Active)
        {
            return Result.Success();
        }

        // `23-85`: checked once the row is loaded, not before - the command carries no ChannelKind of
        // its own (adr/0069's (site, channel) key lives on the row, not on every caller's request), and
        // a not-found credential should read as not-found regardless of entitlement.
        if (!await ChannelEntitlement.IsEntitledAsync(entitlements, grants, command.SiteId, credential.Kind, cancellationToken))
        {
            return ChannelEntitlement.Refusal(credential.Kind);
        }

        credential.Revoke();
        await credentials.SaveAsync(credential, cancellationToken);

        return Result.Success();
    }
}
