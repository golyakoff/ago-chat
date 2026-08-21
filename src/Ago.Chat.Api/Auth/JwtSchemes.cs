namespace Ago.Chat.Api.Auth;

/// <summary>
/// Two schemes, one signing key: a visitor holds a capability (its token's <c>aud</c> is
/// <see cref="Visitor"/>, scoped to one conversation by claim), an operator holds a role
/// (<see cref="Operator"/>, checked against adr/0016's RBAC per request) - the audience mismatch is
/// what stops a visitor token from ever authenticating against <c>/hubs/operator</c> and back.
/// </summary>
public static class JwtSchemes
{
    public const string Visitor = "Visitor";
    public const string Operator = "Operator";
}
