using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ExportConversation;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ExportVisitor;

/// <summary>
/// `24-11`: the visitor-scoped sibling of <c>ExportConversationHandler</c> - same permission, same
/// rate-limit bucket (<see cref="PersonExportRateLimitOptions"/>, shared rather than duplicated - the
/// expense is a property of the site being exported, not of which of the two routes an operator called),
/// same not-found-not-forbidden cross-tenant guard. The one real difference: this resolves every
/// conversation the visitor has, not only the one named in the route.
///
/// <para><b>Not gated on the visitor holding a <see cref="ChannelIdentity"/>, unlike
/// <c>GetVisitorHistoryHandler</c>'s operator panel.</b> That gate exists to answer a different
/// question - which operator may read historical messages on a conversation they were never assigned
/// to (`18-07`'s own remarks: "a widget visitor's history is structurally unreachable" is a
/// feature-availability choice for that panel, not a technical impossibility). An export is a
/// completeness question, not an authorization-scope one: a widget-only visitor can still have more
/// than one historical conversation under the same <see cref="VisitorId"/> (closed, then a new one
/// started later, all on the same browser), and every one of them is this same person's own data, so
/// this handler includes all of them regardless of whether a <see cref="ChannelIdentity"/> exists.
/// <see cref="IConversationReadStore.ListAllForVisitorAsync"/> naturally returns exactly one id for the
/// common case (a visitor who has only ever had one conversation) and more for a returning or
/// channel-identified one - no special-casing needed either way.</para>
/// </summary>
public sealed class ExportVisitorHandler(
    IConversationReadStore conversations,
    IPersonExportArchiveWriter archiveWriter,
    IRateLimiter rateLimiter,
    IPermissionChecker permissions,
    PersonExportRateLimitOptions rateLimitOptions,
    IClock clock)
{
    public async Task<Result<PersonExportArchive>> HandleAsync(ExportVisitor command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationExport, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to export this visitor's data.");
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

        var conversationIds = await conversations.ListAllForVisitorAsync(conversation.VisitorId, cancellationToken);

        var exportedAt = clock.UtcNow;
        var stream = await archiveWriter.WriteAsync(
            command.SiteId, conversation.VisitorId, conversationIds, "visitor", exportedAt, cancellationToken);

        var fileName = $"visitor-{conversation.VisitorId.Value:N}-export-{exportedAt:yyyyMMdd}.zip";
        return new PersonExportArchive(stream, fileName);
    }
}
