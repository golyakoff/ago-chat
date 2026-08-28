using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RequestSiteErasure;

/// <summary>
/// `16-02`: the whole-account deletion request. Sets <c>sites.erasure_requested_at</c> in one
/// statement and does <b>no</b> deletion work here - "deletion is a job, not a request handler"
/// (`16-02-erasure-account-and-conversation.md`'s own Scope), because these writes touch many rows
/// across several stores (Postgres, MinIO, Keycloak) and can fail halfway; a synchronous HTTP call a
/// timeout can tear in half is exactly the shape that must not happen here.
/// `Ago.Chat.Worker`'s <c>SiteErasureJob</c> is what actually removes anything, off its own timer.
///
/// <para>Gated by <see cref="Permission.SiteErase"/>, not <see cref="Permission.SiteConfigure"/> - see
/// that permission's own remarks for why a single, dedicated permission earns its place here rather
/// than folding into the existing, broader one.</para>
/// </summary>
public sealed class RequestSiteErasureHandler(
    IErasureRequestRepository erasureRequests, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result> HandleAsync(RequestSiteErasure command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteErase, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to erase this site.");
        }

        var found = await erasureRequests.RequestSiteErasureAsync(
            command.SiteId, clock.UtcNow, cancellationToken);
        if (!found)
        {
            return ConversationErrors.SiteNotFound(command.SiteId.Value);
        }

        return Result.Success();
    }
}
