using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.UseCases.GetTeamMessageHistory;

/// <summary>No permission check, the read-side mirror of
/// <c>SendTeamMessageHandler</c>'s own "every operator of the site" reasoning - a site's operator
/// claim is what scopes this query to exactly one room, and there is no narrower or wider read to
/// forbid.</summary>
public sealed class GetTeamMessageHistoryHandler(ITeamMessageReadStore readStore)
{
    public Task<TeamMessageHistoryPage> HandleAsync(GetTeamMessageHistory query, CancellationToken cancellationToken) =>
        readStore.GetHistoryAsync(query.SiteId, query.BeforeSequence, query.PageSize, cancellationToken);

    public Task<IReadOnlyList<TeamMessageHistoryItem>> HandleDeltaAsync(
        GetTeamMessageDelta query, CancellationToken cancellationToken) =>
        readStore.GetDeltaAsync(query.SiteId, query.AfterSequence, cancellationToken);
}
