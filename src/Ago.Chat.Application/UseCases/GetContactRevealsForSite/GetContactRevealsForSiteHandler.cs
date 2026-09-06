using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetContactRevealsForSite;

/// <summary>
/// `23-11`/`decisions.md` §5's own warning, carried into this handler's placement rather than
/// implemented: "reveal counts belong in an audit view, never in the report a person is judged on."
/// This *is* that audit view - a separate, deliberately unaggregated list of individual reveals, never
/// a per-operator count a staff-comparison screen could quote. Nothing in this codebase's operator
/// analytics (`18-08`) reads this table, and this item adds no such reader.
///
/// <para>Gated on <see cref="Permission.SiteConfigure"/>, not <see cref="Permission.ConversationRead"/> -
/// the same reasoning `GetAccessRecordsForSiteHandler`'s own remarks give for its sibling audit read:
/// every operator who can reveal a contact detail is not thereby trusted to see the whole tenant's own
/// reveal history, which is a materially wider read (every conversation's reveals, not just the ones
/// this operator is a party to). <c>SiteConfigure</c> already gates the other tenant-facing admin
/// reports this codebase has (`access-records`, `message-archives`) - the same class of capability,
/// not a new one.</para>
/// </summary>
public sealed class GetContactRevealsForSiteHandler(IContactRevealRepository reveals, IPermissionChecker permissions)
{
    internal const int DefaultLimit = 50;

    internal const int MaxLimit = 200;

    public async Task<Result<ContactRevealPage>> HandleAsync(
        GetContactRevealsForSite query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read this site's contact reveal record.");
        }

        var limit = Math.Clamp(query.Limit ?? DefaultLimit, 1, MaxLimit);

        var page = await reveals.ListForSiteAsync(query.SiteId, query.Before, limit, cancellationToken);
        return page;
    }
}
