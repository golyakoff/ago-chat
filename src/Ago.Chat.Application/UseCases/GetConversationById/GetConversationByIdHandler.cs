using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetConversationById;

/// <summary>
/// `16-02`: a real gap this item found rather than one it scoped - there was no single-conversation
/// admin-fetch endpoint anywhere in this codebase (`GetConversationHistoryHandler` reads a
/// conversation's messages and is only ever reached through `OperatorHub`/`VisitorHub`, not REST; the
/// closest REST neighbour, `GetAllConversationsForSiteHandler`, pages every conversation for a site,
/// not one by id). Built because `16-02`'s own Done-when needs it: "the console does not report
/// completion before the job has completed" has no mechanism to satisfy for conversation erasure
/// without something the console can poll until it 404s - see
/// `Ago.Chat.Api`'s `ConversationsEndpoints` for the route this backs.
///
/// <para>Gated by <see cref="Permission.ConversationErase"/>, not <see cref="Permission.ConversationRead"/>
/// - deliberately narrow rather than a general-purpose "fetch any conversation by id" capability this
/// item was never asked to build. The one real caller is the erasure completion poll, and only an
/// operator who could have requested the erasure (the same permission
/// <c>RequestConversationErasureHandler</c> checks) has a legitimate reason to poll for it - widening
/// this to <c>ConversationRead</c> would also be semantically wrong for the poll's other property:
/// `GetConversationHistoryHandler`'s own operator path additionally requires being *the assigned
/// operator*, which the caller polling after requesting an Admin-scoped erasure need not be.</para>
/// </summary>
public sealed class GetConversationByIdHandler(IConversationReadStore readStore, IPermissionChecker permissions)
{
    public async Task<Result<ConversationSummaryItem>> HandleAsync(
        GetConversationById query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationErase, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view this conversation.");
        }

        var conversation = await readStore.GetByIdAsync(query.ConversationId, query.SiteId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        return conversation;
    }
}
