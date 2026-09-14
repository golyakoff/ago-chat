using Ago.Chat.Application.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetChannelCredentialStatus;

/// <summary>
/// `23-36`: the read-side counterpart to <c>RegisterChannelCredentialHandler</c>/
/// <c>RevokeChannelCredentialHandler</c> - same permission, same repository, same
/// <c>(SiteId, ChannelKind)</c> scoping, so a caller who lacks <see cref="Domain.Permission.ChannelManage"/>
/// for this site learns nothing, and a caller who holds it for a <em>different</em> site cannot reach
/// this one's credential at all: <see cref="IChannelCredentialRepository.GetActiveAsync"/> is scoped by
/// <see cref="Domain.SiteId"/> at the query itself, not filtered afterward, so there is no row to leak
/// even before the permission check runs.
///
/// <para><b>`23-85`/`adr/0151`: also the read half of "23-36's live status read stops for a lapsed
/// entitlement" (the item's own Scope, point 5).</b> Checking the entitlement here, before this method
/// ever returns, is what actually stops <c>TelegramChannelEndpoints.HandleStatusAsync</c> from calling
/// Telegram's own <c>getMe</c> at all: that endpoint calls this handler first and returns its error
/// immediately on failure, never reaching <c>TelegramLiveTokenCheck.RunAsync</c> below it. No change
/// to that endpoint was needed for this item's own "a non-paying account no longer costs a third
/// party's rate limit" - the existing "ask this handler first" sequencing already does it, once this
/// handler itself refuses.</para>
/// </summary>
public sealed class GetChannelCredentialStatusHandler(
    IChannelCredentialRepository credentials, IPermissionChecker permissions,
    IBillingOptionEntitlementProvider entitlements, IModuleQuantityGrantStore grants)
{
    public async Task<Result<ChannelCredentialStatus>> HandleAsync(
        GetChannelCredentialStatus query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Domain.Permission.ChannelManage, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage channels for this site.");
        }

        if (!await ChannelEntitlement.IsEntitledAsync(entitlements, grants, query.SiteId, query.Kind, cancellationToken))
        {
            return ChannelEntitlement.Refusal(query.Kind);
        }

        var credential = await credentials.GetActiveAsync(query.SiteId, query.Kind, cancellationToken);

        return credential is null
            ? new ChannelCredentialStatus(null, null)
            : new ChannelCredentialStatus(credential.Id, credential.CreatedAt);
    }
}
