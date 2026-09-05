using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RequestConversationErasure;

/// <summary>
/// `16-02`: the narrower sibling of <c>RequestSiteErasureHandler</c> - "a tenant deletes one visitor's
/// conversation on that visitor's request" (`16-02-erasure-account-and-conversation.md`'s own Goal).
/// Same shape: one flag set, no deletion performed here, <c>Ago.Chat.Worker</c>'s
/// <c>ConversationErasureJob</c> does the actual removal off its own timer.
///
/// <para>Gated by <see cref="Permission.ConversationErase"/> - deliberately not
/// <see cref="Permission.ConversationClose"/> or <see cref="Permission.SiteErase"/>. Closing ends a
/// conversation; erasing destroys it, irreversibly, which is a materially different and strictly
/// larger blast radius that deserves its own permission the same way <see cref="Permission.SiteErase"/>
/// does relative to <see cref="Permission.SiteConfigure"/>. Not <see cref="Permission.SiteErase"/>
/// either: an operator trusted to delete one conversation on a visitor's request should not thereby
/// also be able to destroy the whole tenant.</para>
///
/// <para><see cref="IErasureRequestRepository.RequestConversationErasureAsync"/> is scoped by
/// <c>SiteId</c> as well as <c>ConversationId</c>, so a conversation belonging to a different site
/// answers <c>Conversation.NotFound</c> here, never <c>Conversation.Forbidden</c> - the same
/// not-found-not-forbidden choice every other per-conversation check in this codebase makes for a
/// resource outside the caller's tenant (existence itself is not exposed cross-tenant).</para>
///
/// <para><b>`24-13`: also mints this erasure's own receipt id</b> - see
/// <see cref="Ago.Chat.Application.UseCases.RequestSiteErasure.RequestSiteErasureHandler"/>'s own
/// remarks for the identical reasoning applied to the site-scoped sibling.</para>
/// </summary>
public sealed class RequestConversationErasureHandler(
    IErasureRequestRepository erasureRequests, IPermissionChecker permissions, IIdGenerator idGenerator, IClock clock)
{
    public async Task<Result> HandleAsync(RequestConversationErasure command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationErase, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to erase conversations for this site.");
        }

        var now = clock.UtcNow;
        var erasureRecordId = idGenerator.NewId(now);

        var found = await erasureRequests.RequestConversationErasureAsync(
            command.ConversationId, command.SiteId, command.RequestedBy, erasureRecordId, now, cancellationToken);
        if (!found)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        return Result.Success();
    }
}
