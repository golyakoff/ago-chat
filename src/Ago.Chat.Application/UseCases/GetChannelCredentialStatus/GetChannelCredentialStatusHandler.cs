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
/// </summary>
public sealed class GetChannelCredentialStatusHandler(IChannelCredentialRepository credentials, IPermissionChecker permissions)
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

        var credential = await credentials.GetActiveAsync(query.SiteId, query.Kind, cancellationToken);

        return credential is null
            ? new ChannelCredentialStatus(null, null)
            : new ChannelCredentialStatus(credential.Id, credential.CreatedAt);
    }
}
