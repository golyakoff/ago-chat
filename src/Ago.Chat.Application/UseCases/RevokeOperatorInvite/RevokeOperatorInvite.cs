using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RevokeOperatorInvite;

/// <summary>`25-73`: the console's own "отозвать" button - <paramref name="SiteId"/> is the caller's
/// own active site (from their `OperatorId` claim's tenancy, matching every other write in this
/// handler's own folder-mates), compared against the loaded invite's own <see cref="OperatorInvite.SiteId"/>
/// rather than trusted from the request path alone - the same "wrong tenant reads like no such row"
/// info-hiding shape this codebase already applies to every other cross-tenant id lookup.</summary>
public sealed record RevokeOperatorInvite(OperatorId RequestedBy, SiteId SiteId, OperatorInviteId OperatorInviteId);
