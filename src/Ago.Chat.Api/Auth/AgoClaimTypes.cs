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

    /// <summary>`17-06`: the two values <see cref="Kind"/> is ever allowed to hold, named rather than
    /// spelled out at each of the four sites that used to repeat the literal (the two that *write* the
    /// claim - <c>JwtTokenService.IssueVisitorToken</c> and
    /// <c>OperatorIdentityClaimsTransformation</c> - and the two that *read* it -
    /// <c>ClaimsPrincipalExtensions.IsOperator</c> and <c>AttachmentEndpoints</c>'s own policy). A
    /// principal on the shared attachment route now has to carry one of exactly these two; see that
    /// policy's own remarks for the third state that made the closed set worth stating.</summary>
    public const string VisitorKind = "visitor";

    /// <inheritdoc cref="VisitorKind"/>
    public const string OperatorKind = "operator";
}
