namespace Ago.Chat.Api.Auth;

internal static class AgoClaimTypes
{
    public const string SiteId = "site_id";

    /// <summary>`5-05`/`adr/0022`: added by `OperatorIdentityClaimsTransformation`, never present on
    /// a token as issued - a Keycloak-signed token's own `sub` is Keycloak's subject id, not this
    /// project's `OperatorId`, so `ClaimsPrincipalExtensions.GetOperatorId` reads this claim instead
    /// of `sub` for the Operator scheme.</summary>
    public const string OperatorId = "operator_id";

    /// <summary>`5-03`: the one thing `aud` alone cannot answer from inside a request handler - which
    /// kind of principal actually authenticated - for the one place that needed it,
    /// <c>AttachmentEndpoints</c>, whose routes accept either a visitor or an operator token
    /// (`AddAuthenticationSchemes(JwtSchemes.Visitor, JwtSchemes.Operator)`) and must branch on which
    /// one won without re-deriving it from claim shape. Every other endpoint/hub stays single-scheme
    /// and never needed this.</summary>
    public const string Kind = "kind";
}
