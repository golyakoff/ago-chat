using Ago.Chat.Application.Abstractions;

namespace Ago.Chat.Application.UseCases.ResolveOperatorIdentity;

/// <summary>
/// `5-05`: the one lookup `Ago.Chat.Api`'s `IClaimsTransformation` needs, called once per request for
/// a validated Keycloak token - `adr/0022`'s own "not cached" call, since `PermissionChecker` already
/// pays an equivalent per-request database read on the same path and this is not a new order of
/// magnitude.
/// </summary>
public sealed class ResolveOperatorIdentityHandler(IOperatorRepository operators)
{
    public async Task<OperatorIdentity?> HandleAsync(ResolveOperatorIdentityQuery query, CancellationToken cancellationToken)
    {
        var operatorEntity = await operators.GetByExternalSubjectIdAsync(query.ExternalSubjectId, cancellationToken);
        return operatorEntity is null ? null : new OperatorIdentity(operatorEntity.Id, operatorEntity.SiteId);
    }
}
