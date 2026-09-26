using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetConversationHistoryAsSiteConfigureHolder;

/// <summary>
/// `26-98`: the «Все» list's own "open one" - Option 1 of the three readings
/// `docs/backlog/26-98-*.md` laid out, decided by the author. Before this, the only way to read a
/// conversation's message history as an operator was
/// <c>GetConversationHistory.GetConversationHistoryHandler.HandleAsOperatorAsync</c> /
/// <c>OperatorHub.JoinConversationAsync</c>, both of which check
/// <c>conversation.OperatorId == RequestedBy</c> - true only for the operator actually assigned to it.
/// <c>GetAllConversationsForSite.GetAllConversationsForSiteHandler</c> (`5-08`/`26-90`) already lists
/// every conversation on the site for a <see cref="Permission.SiteConfigure"/> holder, admin-wide, with
/// no such assignment - opening one from that list hit the identical assignment check on the join path
/// and either claimed a <c>Waiting</c> row out from under the queue (<c>Conversation.AssignTo</c>'s own
/// side effect) or threw for every other state, which is most of that list
/// (`docs/backlog/26-98-*.md`'s own "What is actually true today"). This handler is the read that list
/// was always missing: the identical <see cref="Permission.SiteConfigure"/> gate the list itself already
/// uses, no assignment check, and no call to <c>Conversation.AssignTo</c> anywhere in it - a holder of
/// that permission genuinely only reads.
///
/// <para><b>Not a third entry point on <c>GetConversationHistory.GetConversationHistoryHandler</c>.</b>
/// That class's own doc comment already reasons about two entry points sharing one fetch because they
/// "differ only in how access is checked" - true of visitor-vs-operator, and true of `26-144`'s own
/// visitor-history "open one" sitting beside it in a *different* handler for the identical reason its
/// own remarks give (a genuinely different, new access rule earns its own type rather than a parameter
/// on an existing one). This handler's access rule is newer still: no assignment, no shared-visitor
/// requirement, just the site-wide permission <c>GetAllConversationsForSiteHandler</c> already uses.
/// Keeping it a fourth, standalone handler rather than reshaping either existing class is what keeps
/// this item's own blast radius to one new file plus one new hub method - every existing caller of
/// <c>GetConversationHistoryHandler</c>/<c>GetVisitorHistoryHandler</c> and their tests are
/// untouched.</para>
///
/// <para><b>The explicit site check (<see cref="Domain.Conversation.SiteId"/> against
/// <see cref="GetConversationHistoryAsSiteConfigureHolderQuery.SiteId"/>) is load-bearing here in a way it
/// never had to be on the handlers above.</b> Every existing operator-facing read is anchored by an
/// assignment (<c>conversation.OperatorId == RequestedBy</c>), and an operator is only ever assigned
/// conversations on their own site - so a cross-tenant id was already unreachable by construction, with
/// nothing here ever checking it directly. This handler has no assignment to lean on, so without this
/// check a <see cref="Permission.SiteConfigure"/> holder on one tenant could read any conversation on any
/// tenant by guessing an id - checked here and refused as <see cref="ConversationErrors.NotFound"/>, the
/// same "wrong tenant reads like no row" info-hiding shape this codebase already uses for a cross-tenant
/// id elsewhere.</para>
///
/// <para><b>Deliberately a snapshot, not a subscription.</b> This never adds the caller's connection to
/// any delivery target the way <c>OperatorHub.JoinConversationAsync</c> implicitly does by assigning - a
/// message sent to this conversation while this read-only view is open does not arrive live, the same
/// limitation <c>GetVisitorHistoryConversationAsync</c>'s own historical read already carries. Reported
/// as a scope finding, not silently assumed: a supervisor reviewing «Все» gets an accurate paginated
/// history, not a second live inbox.</para>
///
/// <para><b>No `access_records` row.</b> `24-12`'s own vocabulary (<see cref="Domain.AccessRecordKind"/>)
/// is "the defensible set the backlog item names, not one member per permission-gated endpoint" - this
/// item's own Scope asks only for the read path and the Android mode, and adding a new boundary-crossing
/// kind here would also touch `docs/architecture/personal-data.md`, which lives outside the worktrees
/// this item was scoped to touch. Recorded as a real, deliberate gap for the author to weigh against
/// `CrossConversationHistoryRead`'s own precedent (an operator reading a conversation they were never a
/// party to) - not silently decided in this handler's own code.</para>
/// </summary>
public sealed class GetConversationHistoryAsSiteConfigureHolderHandler(
    IConversationRepository conversations, IConversationReadStore readStore, IPermissionChecker permissions)
{
    public async Task<Result<ConversationHistoryPage>> HandleAsync(
        GetConversationHistoryAsSiteConfigureHolderQuery query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to view every conversation for this site.");
        }

        var conversation = await conversations.GetByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        // See this type's own remarks: with no assignment check below, this is the only thing standing
        // between "reads this tenant's own conversations" and "reads any tenant's conversation by id".
        if (conversation.SiteId != query.SiteId)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        // `24-10`: unreachable, not merely hidden - the same rule every other operator-facing read in
        // this codebase already gives a blocked conversation (`GetConversationHistoryHandler`'s own
        // identical check).
        if (conversation.IsBlocked)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        var page = await readStore.GetHistoryAsync(
            query.ConversationId, conversation.SiteId, query.BeforeSequence, query.PageSize, cancellationToken);
        return page;
    }
}
