using System.Security.Claims;
using Ago.Chat.Api.Auth;
using Ago.Chat.Domain;
using Microsoft.AspNetCore.Authorization;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `12-01`/`adr/0032`: the fail-closed half of the platform-owner boundary, exercised directly
/// against <see cref="PlatformOwnerAuthorizationHandler"/> rather than through a real Keycloak.
/// <see cref="PlatformOwnerPolicyTests"/> proves the happy path with genuine, Keycloak-signed tokens;
/// what *that* cannot reach is the malformed and hostile shapes a real Keycloak will never mint - a
/// `realm_access` claim that is not JSON, whose `roles` is not an array, or that is absent entirely.
/// Those are exactly the shapes where "deny" has to be the answer by construction, so they get a
/// container-free test at the level the decision is actually made, matching
/// <see cref="WebhookSecretCipherTests"/>'s own precedent for a pure-logic test living in this project.
/// </summary>
public sealed class PlatformOwnerAuthorizationHandlerTests
{
    private const string RealmAccess = PlatformOwnerAuthorizationHandler.RealmAccessClaimType;

    [Fact]
    public async Task RealmAccessCarryingTheOwnerRole_Succeeds()
    {
        var succeeded = await EvaluateAsync(new Claim(RealmAccess, """{"roles":["platform-owner"]}"""));

        Assert.True(succeeded);
    }

    [Fact]
    public async Task RealmAccessCarryingTheOwnerRoleAmongOthers_Succeeds()
    {
        var succeeded = await EvaluateAsync(
            new Claim(RealmAccess, """{"roles":["offline_access","platform-owner","uma_authorization"]}"""));

        Assert.True(succeeded);
    }

    [Fact]
    public async Task NoRealmAccessClaimAtAll_Fails()
    {
        var context = await HandleAsync(new Claim("sub", Guid.NewGuid().ToString()));

        Assert.False(context.HasSucceeded);
        // Not merely "did not succeed": an explicit Fail is sticky for the whole policy evaluation,
        // so a second handler registered for this requirement later cannot grant what this denied.
        Assert.True(context.HasFailed);
    }

    /// <summary>The shape a hostile or simply broken token would have. Denying is the only safe
    /// reading; throwing would turn it into a 500 and hand the caller a signal that the claim was
    /// parsed at all.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("platform-owner")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("""["platform-owner"]""")]
    [InlineData("{}")]
    [InlineData("""{"roles":null}""")]
    [InlineData("""{"roles":"platform-owner"}""")]
    [InlineData("""{"roles":{"0":"platform-owner"}}""")]
    [InlineData("""{"roles":[["platform-owner"]]}""")]
    [InlineData("""{"realm":{"roles":["platform-owner"]}}""")]
    public async Task MalformedRealmAccess_Fails(string realmAccessValue)
    {
        var context = await HandleAsync(new Claim(RealmAccess, realmAccessValue));

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }

    /// <summary>Keycloak role names are case-sensitive, and only the exact name this project's
    /// realm-import files define is the owner role - a near miss is a different role.</summary>
    [Theory]
    [InlineData("Platform-Owner")]
    [InlineData("PLATFORM-OWNER")]
    [InlineData("platform_owner")]
    [InlineData("platform-owner-readonly")]
    [InlineData("ago-platform-owner")]
    [InlineData(" platform-owner ")]
    public async Task ARoleThatIsNotExactlyTheOwnerRole_Fails(string roleName)
    {
        var succeeded = await EvaluateAsync(new Claim(RealmAccess, $$"""{"roles":["{{roleName}}"]}"""));

        Assert.False(succeeded);
    }

    /// <summary>The structural claim `adr/0032` rests on, written as a test: nothing this codebase
    /// can put on a principal - not `operator_id`, not `site_id`, not every `Permission` it defines,
    /// including `site:configure` - moves the handler off "deny." The only input it reads is a claim
    /// Keycloak signs, which no write to `roles`/`operator_roles` can produce.</summary>
    [Fact]
    public async Task APrincipalHoldingEveryPermissionThisProjectDefines_StillFails()
    {
        Claim[] claims =
        [
            new(AgoClaimTypes.OperatorId, Guid.NewGuid().ToString()),
            new(AgoClaimTypes.SiteId, Guid.NewGuid().ToString()),
            new(AgoClaimTypes.Kind, "operator"),
            new(Permission.SiteConfigure.Value, "true"),
            new(Permission.SiteManageOperators.Value, "true"),
            new(Permission.AttachmentDelete.Value, "true"),
            new(Permission.ConversationRead.Value, "true"),
            new(Permission.ConversationSend.Value, "true"),
            new(Permission.ConversationAssign.Value, "true"),
            new(Permission.ConversationClose.Value, "true"),
            new(Permission.WebhookManage.Value, "true"),
            new(ClaimTypes.Role, "Admin"),
            new("roles", "platform-owner"),
        ];

        var succeeded = await EvaluateAsync(claims);

        Assert.False(succeeded);
    }

    /// <summary>Defence in depth behind `Program.cs`'s own `RequireAuthenticatedUser()`: even handed
    /// a principal that somehow carries the claim without having authenticated, the handler denies.</summary>
    [Fact]
    public async Task AnUnauthenticatedPrincipalCarryingTheClaim_Fails()
    {
        // No authenticationType - ClaimsIdentity.IsAuthenticated is false.
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(RealmAccess, """{"roles":["platform-owner"]}""")]));

        var context = await HandleAsync(principal);

        Assert.False(context.HasSucceeded);
        Assert.True(context.HasFailed);
    }

    /// <summary>A principal can carry more than one identity in this application
    /// (`OperatorIdentityClaimsTransformation` adds a second one), so the handler must not answer
    /// from whichever identity happens to be first.</summary>
    [Fact]
    public async Task TheOwnerClaimOnASecondIdentity_Succeeds()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("sub", Guid.NewGuid().ToString())], "TestKeycloak"));
        principal.AddIdentity(new ClaimsIdentity(
            [new Claim(RealmAccess, """{"roles":["platform-owner"]}""")], "TestKeycloak"));

        var context = await HandleAsync(principal);

        Assert.True(context.HasSucceeded);
    }

    private static async Task<bool> EvaluateAsync(params Claim[] claims) =>
        (await HandleAsync(claims)).HasSucceeded;

    private static Task<AuthorizationHandlerContext> HandleAsync(params Claim[] claims) =>
        HandleAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, "TestKeycloak")));

    private static async Task<AuthorizationHandlerContext> HandleAsync(ClaimsPrincipal principal)
    {
        var requirement = new PlatformOwnerRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal, resource: null);

        await new PlatformOwnerAuthorizationHandler().HandleAsync(context);

        return context;
    }
}
