using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ExportConversation;

/// <summary>
/// `24-11`: hands back one conversation's export archive, built and ready before this method returns -
/// unlike `16-03`'s `RequestSiteExportHandler`, there is no `Pending` row and no completion poll. See
/// <see cref="IPersonExportArchiveWriter"/>'s own remarks for why this scope does not need the
/// asynchronous job shape a whole-site export does.
///
/// <para>Gated by <see cref="Permission.ConversationExport"/>, checked before anything else -
/// <see cref="RequestConversationErasure.RequestConversationErasureHandler"/>'s own ordering, applied
/// to the identical reason: a caller with no export permission on this site must never be able to
/// spend a share of the rate limiter's shared budget finding that out.</para>
///
/// <para><b>No separate operator-assignment check.</b> Unlike <c>GetVisitorHistoryHandler</c>/
/// <c>ListChannelIdentitiesForVisitorHandler</c> (which require the caller to be the conversation's own
/// assigned operator), this follows <c>RequestConversationErasureHandler</c>'s shape instead - an
/// Admin-role permission scoped to the site, not to "my own assigned conversation." The backlog item's
/// own instruction is "the same permission thinking the erasure endpoints already use," and erasure's
/// conversation-scoped handler is the direct sibling this mirrors, not the read-side panels, which
/// exist to answer a different question (which operator may read a conversation they were never
/// assigned to) that does not apply to an Admin-gated export.</para>
///
/// <para><see cref="IConversationReadStore.GetByIdAsync"/> is what enforces cross-tenant isolation -
/// scoped by <c>siteId</c> as well as <c>conversationId</c>, so a conversation belonging to a different
/// site answers <see cref="ConversationErrors.NotFound"/> here, never a Forbidden that would confirm
/// the id exists at all (the same not-found-not-forbidden choice `adr/0072` already establishes for
/// `16-03`'s own export status poll).</para>
/// </summary>
public sealed class ExportConversationHandler(
    IConversationReadStore conversations,
    IPersonExportArchiveWriter archiveWriter,
    IRateLimiter rateLimiter,
    IPermissionChecker permissions,
    PersonExportRateLimitOptions rateLimitOptions,
    IClock clock)
{
    public async Task<Result<PersonExportArchive>> HandleAsync(
        ExportConversation command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationExport, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to export this conversation.");
        }

        var limit = await rateLimiter.CheckAsync(
            new RateLimitKey($"person-export:site:{command.SiteId.Value}"),
            new RateLimitRule(rateLimitOptions.PerSiteCapacity, rateLimitOptions.PerSiteRefillPerSecond),
            cancellationToken);
        if (!limit.Allowed)
        {
            return ConversationErrors.PersonExportRateLimited(limit.RetryAfter);
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, command.SiteId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        var exportedAt = clock.UtcNow;
        var stream = await archiveWriter.WriteAsync(
            command.SiteId, conversation.VisitorId, [command.ConversationId], "conversation", exportedAt, cancellationToken);

        var fileName = $"conversation-{command.ConversationId.Value:N}-export-{exportedAt:yyyyMMdd}.zip";
        return new PersonExportArchive(stream, fileName);
    }
}
