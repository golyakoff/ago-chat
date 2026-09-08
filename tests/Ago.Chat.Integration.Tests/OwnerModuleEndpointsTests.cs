using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Modules;
using Ago.Chat.Api.Owner;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.EnableModuleForSiteAsOwner;
using Ago.Chat.Application.UseCases.GrantModuleQuantityAsOwner;
using Ago.Chat.Application.UseCases.ListEnabledModulesForSite;
using Ago.Chat.Application.UseCases.ResolveOperatorIdentity;
using Ago.Chat.Application.UseCases.RevokeModuleForSiteAsOwner;
using Ago.Chat.Application.UseCases.RotateModuleCredentialAsOwner;
using Ago.Chat.Application.UseCases.VerifyModuleRegistrationAsOwner;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Modules;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `22-17`'s own Done-when, over a real HTTP pipeline, a real Postgres and real Keycloak-signed
/// tokens (<see cref="OperatorOidcFixture"/>): the platform owner can grant a module to a tenant with
/// no payment, the grant is distinguishable from a self-service purchase wherever either is recorded,
/// revoking it works, and neither an ordinary operator nor a site-wide admin can reach this surface.
/// `23-83`/`adr/0151` adds the platform owner's own rotate and verify to the same claim: the two
/// writes `22-11` never gave the owner, reachable only once the tenant's own copies stopped existing
/// as routes.
///
/// <para>Both <see cref="ModuleEndpoints.MapModuleEndpoints"/> (the tenant's own read-only route,
/// `RequireOperatorIdentity` - `23-83` removed its four writes) and
/// <see cref="OwnerModuleEndpoints.MapOwnerModuleEndpoints"/> (the owner's route,
/// `RequirePlatformOwner`) are mapped on the same host in this file - deliberately, because the
/// audit-distinction claim can only be proven by comparing what the owner's own writes produce
/// against what the tenant's own read shows, not by reading either route's own behaviour in
/// isolation. What used to be a second write path to compare against
/// (<see cref="SeedSelfServicePurchaseAsync"/>'s own remarks) is now a direct Postgres seed instead -
/// the tenant no longer has a route that could produce one.</para>
/// </summary>
[Collection(OperatorOidcCollection.Name)]
public sealed class OwnerModuleEndpointsTests(OperatorOidcFixture fixture)
{
    private string SelfServiceRoute => $"/api/v1/sites/{fixture.SeededSiteId.Value}/modules";

    private string OwnerRoute => $"/api/v1/owner/sites/{fixture.SeededSiteId.Value}/modules";

    /// <summary>
    /// The item's own first Done-when, end to end: a call the tenant could not make before (their
    /// module list is empty for this key) succeeds after the owner grants it - through the real
    /// route, with no row inserted by hand on either side of the assertion.
    /// </summary>
    [Fact]
    public async Task OwnerToken_GrantsAModule_AndTheTenantsOwnListingShowsIt()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        // Before: the tenant's own module list has no such key.
        var before = await GetModulesAsync(operatorClient);
        Assert.DoesNotContain(before.Modules, m => m.ModuleKey == moduleKey);

        var grantResponse = await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/owner-granted"],
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });
        Assert.Equal(HttpStatusCode.OK, grantResponse.StatusCode);

        // After: the identical call, through the tenant's own operator-facing GET, now lists it -
        // proving the grant is real, not merely accepted by the owner's own route.
        var after = await GetModulesAsync(operatorClient);
        var granted = Assert.Single(after.Modules, m => m.ModuleKey == moduleKey);
        Assert.Equal(["/owner-granted"], granted.TriggerWords);
    }

    /// <summary>The audit distinction, proven by doing both and comparing - not asserted from one
    /// side alone. A self-service enable and an owner grant land in the identical listing with the
    /// identical shape, distinguished only by <see cref="ModuleEndpoints.EnableModuleResponse.GrantedByOwner"/>.</summary>
    [Fact]
    public async Task GrantedByOwner_DistinguishesAnOwnerGrant_FromATenantsOwnPurchase()
    {
        var purchasedKey = UniqueModuleKey();
        var grantedKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        // The tenant bought module A themselves, at some point before `23-83` removed the self-service
        // route that once made that possible - seeded directly, the same "a real row, not a live call"
        // shape `ModuleEndpointsTests.DemoAdminToken_CannotListAnotherTenantsEnabledModules` uses for
        // its own victim row (`SeedSelfServicePurchaseAsync`'s own remarks).
        await SeedSelfServicePurchaseAsync(purchasedKey, "/purchased");

        // The owner grants module B, with no payment, through the owner-only route.
        var grant = await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = grantedKey,
                TriggerWords = ["/granted"],
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
        Assert.Equal(HttpStatusCode.OK, grant.StatusCode);

        var modules = await GetModulesAsync(operatorClient);
        var purchasedRow = Assert.Single(modules.Modules, m => m.ModuleKey == purchasedKey);
        var grantedRow = Assert.Single(modules.Modules, m => m.ModuleKey == grantedKey);

        Assert.False(purchasedRow.GrantedByOwner);
        Assert.Null(purchasedRow.ExpiresAt);
        Assert.True(grantedRow.GrantedByOwner);
        Assert.NotNull(grantedRow.ExpiresAt);
    }

    /// <summary>The item's own second Done-when: revoking works and is proven by trying it, not
    /// asserted. Granted through the owner's own route, revoked through the owner's own route, with
    /// no operator credentials involved anywhere in this test - the platform owner does not borrow a
    /// tenant's own permission to undo what it granted.</summary>
    [Fact]
    public async Task OwnerToken_RevokesAGrant_AndTheTenantsOwnListingNoLongerShowsIt()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/to-be-revoked"],
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });

        var beforeRevoke = await GetModulesAsync(operatorClient);
        Assert.Contains(beforeRevoke.Modules, m => m.ModuleKey == moduleKey);

        var revokeResponse = await ownerClient.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"{OwnerRoute}/{moduleKey}")
        {
            Content = JsonContent.Create(
                new OwnerModuleEndpoints.RevokeModuleAsOwnerRequest()),
        });
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        // Trying it: the entitlement really is gone, not merely reported gone.
        var afterRevoke = await GetModulesAsync(operatorClient);
        Assert.DoesNotContain(afterRevoke.Modules, m => m.ModuleKey == moduleKey);
    }

    // ------------------------------------------------------------------------------------------
    // `23-13`: revoking a tenant's own self-service purchase now needs force and a reason - the
    // asymmetry `flows.md` 5.3 names, proven end to end over the real HTTP pipeline and a real
    // Postgres row, not only at the Application level.
    // ------------------------------------------------------------------------------------------

    /// <summary>The item's own headline Done-when: a tenant's own purchase, revoked through the
    /// owner's route with no force, is refused - and the tenant still has the module.</summary>
    [Fact]
    public async Task OwnerToken_RevokesASelfServicePurchase_WithNoForce_IsRefused_AndTheTenantKeepsIt()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        await SeedSelfServicePurchaseAsync(moduleKey, $"/{moduleKey}");

        var revokeResponse = await ownerClient.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"{OwnerRoute}/{moduleKey}")
        {
            Content = JsonContent.Create(
                new OwnerModuleEndpoints.RevokeModuleAsOwnerRequest()),
        });
        Assert.Equal(HttpStatusCode.Conflict, revokeResponse.StatusCode);

        var modules = await GetModulesAsync(operatorClient);
        Assert.Contains(modules.Modules, m => m.ModuleKey == moduleKey);
    }

    /// <summary>The other half: force and a real reason succeed, the module is really gone, and the
    /// override is recorded with the real platform-owner subject off the real token - not a stand-in
    /// (the identical proof <c>OwnerSitesEndpointTests.OwnerToken_GetsTheCrossTenantList_AndLeavesAnAccessRecord_NamingTheRealOwnerSubject</c>
    /// already gives for its own sibling record).</summary>
    [Fact]
    public async Task OwnerToken_RevokesASelfServicePurchase_WithForceAndAReason_Succeeds_AndRecordsTheOverride()
    {
        var moduleKey = UniqueModuleKey();
        var token = await fixture.GetPlatformOwnerAccessTokenAsync();
        var ownerSubject = new JwtSecurityTokenHandler().ReadJwtToken(token).Subject;

        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, token);
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        await SeedSelfServicePurchaseAsync(moduleKey, $"/{moduleKey}");

        const string reason = "Tenant reported to law enforcement for illegal sales through this module.";
        var revokeResponse = await ownerClient.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"{OwnerRoute}/{moduleKey}")
        {
            Content = JsonContent.Create(
                new OwnerModuleEndpoints.RevokeModuleAsOwnerRequest(
                    Force: true, Reason: reason)),
        });
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        var modules = await GetModulesAsync(operatorClient);
        Assert.DoesNotContain(modules.Modules, m => m.ModuleKey == moduleKey);

        // Trying it, over the real repository against the real row - not asserted from the response
        // alone.
        var overrides = await new ModuleRevokeOverrideRepository(fixture.DataSource)
            .ListForSiteAsync(fixture.SeededSiteId, CancellationToken.None);
        var recorded = Assert.Single(overrides, o => o.ModuleKey == moduleKey);
        Assert.Equal(ownerSubject, recorded.RevokedBy);
        Assert.Equal(reason, recorded.Reason);
    }

    /// <summary>The flag with no reason is refused before anything about the module is touched - the
    /// purchase survives.</summary>
    [Fact]
    public async Task OwnerToken_ForcesARevoke_WithNoReason_IsRefused_AndGrantsNoOverride()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        await SeedSelfServicePurchaseAsync(moduleKey, $"/{moduleKey}");

        var revokeResponse = await ownerClient.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"{OwnerRoute}/{moduleKey}")
        {
            Content = JsonContent.Create(
                new OwnerModuleEndpoints.RevokeModuleAsOwnerRequest(
                    Force: true, Reason: null)),
        });
        Assert.Equal(HttpStatusCode.BadRequest, revokeResponse.StatusCode);

        var modules = await GetModulesAsync(operatorClient);
        Assert.Contains(modules.Modules, m => m.ModuleKey == moduleKey);

        var overrides = await new ModuleRevokeOverrideRepository(fixture.DataSource)
            .ListForSiteAsync(fixture.SeededSiteId, CancellationToken.None);
        Assert.DoesNotContain(overrides, o => o.ModuleKey == moduleKey);
    }

    /// <summary>An owner revoking their own grant with force set and a reason still succeeds - no new
    /// ceremony forced onto that path - but writes no override row, the real-Postgres proof of
    /// <c>RevokeModuleForSiteAsOwnerHandlerTests.HandleAsync_WithForceAndAReason_ForAnOwnerGrant_Succeeds_ButWritesNoOverrideRecord</c>.</summary>
    [Fact]
    public async Task OwnerToken_ForcesARevoke_OfItsOwnGrant_Succeeds_ButRecordsNoOverride()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/owner-granted-forced"],
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });

        var revokeResponse = await ownerClient.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"{OwnerRoute}/{moduleKey}")
        {
            Content = JsonContent.Create(
                new OwnerModuleEndpoints.RevokeModuleAsOwnerRequest(
                    Force: true, Reason: "not actually needed")),
        });
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        var modules = await GetModulesAsync(operatorClient);
        Assert.DoesNotContain(modules.Modules, m => m.ModuleKey == moduleKey);

        var overrides = await new ModuleRevokeOverrideRepository(fixture.DataSource)
            .ListForSiteAsync(fixture.SeededSiteId, CancellationToken.None);
        Assert.DoesNotContain(overrides, o => o.ModuleKey == moduleKey);
    }

    /// <summary>An `ExpiresAt` in the past is refused before anything is granted - the same
    /// "decide, don't default" guard `EnableModuleForSiteAsOwnerHandler` enforces, reached this time
    /// through the real HTTP body.</summary>
    [Fact]
    public async Task OwnerToken_WithAnExpiryInThePast_IsRefused_AndGrantsNothing()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());
        var operatorClient = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        var response = await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/expired"],
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var modules = await GetModulesAsync(operatorClient);
        Assert.DoesNotContain(modules.Modules, m => m.ModuleKey == moduleKey);
    }

    // ------------------------------------------------------------------------------------------
    // `23-66`: the quantity grant this route never had a way to make - `22-07`'s own gap, closed.
    // ------------------------------------------------------------------------------------------

    /// <summary>The item's own headline claim, end to end: the platform owner can raise a module's
    /// granted quantity, and chat's own row (what `ago-calendar`'s consumer will eventually project)
    /// really changed - not merely a `200`.</summary>
    [Fact]
    public async Task OwnerToken_GrantsAQuantity_AndChatsOwnRowReflectsIt()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PutAsJsonAsync(
            $"{OwnerRoute}/{moduleKey}/quantity", new OwnerModuleEndpoints.GrantModuleQuantityRequest(5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerModuleEndpoints.GrantModuleQuantityResponse>();
        Assert.NotNull(body);
        Assert.Equal(moduleKey, body.ModuleKey);
        Assert.Equal(5, body.Quantity);

        var stored = await GetStoredQuantityAsync(moduleKey);
        Assert.Equal(5, stored);
    }

    /// <summary>Zero is a legitimate quantity to grant (this item's own warning) - accepted, not
    /// refused as though it meant nothing.</summary>
    [Fact]
    public async Task OwnerToken_GrantsAZeroQuantity_Succeeds()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PutAsJsonAsync(
            $"{OwnerRoute}/{moduleKey}/quantity", new OwnerModuleEndpoints.GrantModuleQuantityRequest(0));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OwnerToken_GrantsANegativeQuantity_IsRejected()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PutAsJsonAsync(
            $"{OwnerRoute}/{moduleKey}/quantity", new OwnerModuleEndpoints.GrantModuleQuantityRequest(-1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Calling it twice with a different quantity overwrites rather than accumulates - a
    /// snapshot, never a delta (`Domain.ModuleQuantityGrant`'s own remarks), proven here at the wire
    /// rather than only at the store.</summary>
    [Fact]
    public async Task OwnerToken_GrantsAQuantityTwice_TheSecondCallOverwritesTheFirst()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        await ownerClient.PutAsJsonAsync(
            $"{OwnerRoute}/{moduleKey}/quantity", new OwnerModuleEndpoints.GrantModuleQuantityRequest(5));
        var second = await ownerClient.PutAsJsonAsync(
            $"{OwnerRoute}/{moduleKey}/quantity", new OwnerModuleEndpoints.GrantModuleQuantityRequest(2));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var stored = await GetStoredQuantityAsync(moduleKey);
        Assert.Equal(2, stored);
    }

    [Fact]
    public async Task OrdinaryOperatorToken_CannotGrantAQuantity()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            $"{OwnerRoute}/{UniqueModuleKey()}/quantity", new OwnerModuleEndpoints.GrantModuleQuantityRequest(5));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_CannotGrantAQuantity()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PutAsJsonAsync(
            $"{OwnerRoute}/{UniqueModuleKey()}/quantity", new OwnerModuleEndpoints.GrantModuleQuantityRequest(5));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------
    // `23-65`/`adr/0150`: the provisioning secret never reaches the browser - proven over the real
    // HTTP pipeline, not by reading `GrantModuleRequest`'s own shape. `GrantModuleRequest` and
    // `RevokeModuleAsOwnerRequest` no longer have a `ProvisioningSecret` member at all, which already
    // makes it impossible for this file's own well-typed calls above to send one; the two tests below
    // go one step further and send the raw JSON an attacker or a stale client might still try, over
    // the wire, and show the module-registration gateway never sees it.
    // ------------------------------------------------------------------------------------------

    /// <summary>The headline demonstration: a request body carrying the field's old name,
    /// `provisioningSecret`, sent as raw JSON rather than through <see cref="OwnerModuleEndpoints.GrantModuleRequest"/>
    /// (which has no such property to serialize it from). The grant still succeeds - the field is
    /// silently ignored by minimal-API model binding, exactly as an unrecognised member always is -
    /// and the value the module-registration gateway actually receives is
    /// <see cref="ConfiguredProvisioningSecret"/>, this test host's own configured value, never the
    /// one the request body carried.</summary>
    [Fact]
    public async Task OwnerToken_Grants_IgnoresAnySmuggledProvisioningSecretInTheRawBody_AndUsesTheConfiguredOne()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var rawBody = JsonContent.Create(new
        {
            moduleKey,
            triggerWords = new[] { "/smuggled" },
            entryPoint = "https://calendar.example.com",
            credential = "an-owner-minted-secret-of-sixteen-plus-chars",
            expiresAt = (DateTimeOffset?)null,
            provisioningSecret = "an-attacker-supplied-value-of-sixteen-plus-chars",
        });

        var response = await ownerClient.PutAsync(OwnerRoute, rawBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var gateway = (RecordingModuleRegistrationGateway)host.Services.GetRequiredService<IModuleRegistrationGateway>();
        var call = Assert.Single(gateway.RegisterCalls, c => c.Module.ModuleKey.Value == moduleKey);
        Assert.Equal(ConfiguredProvisioningSecret, call.ProvisioningSecret.Value);
        Assert.NotEqual("an-attacker-supplied-value-of-sixteen-plus-chars", call.ProvisioningSecret.Value);
    }

    /// <summary>The identical demonstration for revoke - <see cref="OwnerModuleEndpoints.RevokeModuleAsOwnerRequest"/>
    /// has no `ProvisioningSecret` member either, and a raw body still carrying the field's old name
    /// is silently dropped, not read.</summary>
    [Fact]
    public async Task OwnerToken_Revokes_IgnoresAnySmuggledProvisioningSecretInTheRawBody_AndUsesTheConfiguredOne()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/to-be-revoked-raw"],
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });

        var rawBody = JsonContent.Create(new
        {
            force = false,
            reason = (string?)null,
            provisioningSecret = "an-attacker-supplied-value-of-sixteen-plus-chars",
        });
        var revokeResponse = await ownerClient.SendAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"{OwnerRoute}/{moduleKey}")
        {
            Content = rawBody,
        });
        Assert.Equal(HttpStatusCode.OK, revokeResponse.StatusCode);

        var gateway = (RecordingModuleRegistrationGateway)host.Services.GetRequiredService<IModuleRegistrationGateway>();
        var call = Assert.Single(gateway.RevokeCalls, c => c.Module.ModuleKey.Value == moduleKey);
        Assert.Equal(ConfiguredProvisioningSecret, call.ProvisioningSecret.Value);
        Assert.NotEqual("an-attacker-supplied-value-of-sixteen-plus-chars", call.ProvisioningSecret.Value);
    }

    /// <summary>`adr/0150`'s own deployment-state case, proven over the real HTTP pipeline: a host
    /// that genuinely has not been configured with a provisioning secret refuses the grant with a
    /// clear `503`, rather than sending an empty or missing header the module might itself treat as
    /// "unauthenticated" in some less deliberate way (`adr/0095`'s own "an absent or empty configured
    /// secret never authenticates anything" - the module-registration gateway here is never even
    /// called, so there is no empty header to send in the first place).</summary>
    [Fact]
    public async Task OwnerToken_Grants_WhenThisDeploymentHasNoProvisioningSecretConfigured_Returns503_AndCallsNoGateway()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync(configureProvisioningSecret: false);
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/unconfigured"],
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var gateway = (RecordingModuleRegistrationGateway)host.Services.GetRequiredService<IModuleRegistrationGateway>();
        Assert.Empty(gateway.RegisterCalls);
    }

    /// <summary>`23-92`/`adr/0154`'s own deployment-state case, proven over the real HTTP pipeline: a
    /// host that has not declared an entry point for this module refuses the grant with a clear `503`
    /// naming the module key - never a blank that would only fail later, once the module is actually
    /// called, as a `404` (this item's own brief).</summary>
    [Fact]
    public async Task OwnerToken_Grants_WhenThisDeploymentHasNoEntryPointConfiguredForTheModule_Returns503_AndCallsNoGateway()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync(configureEntryPoint: false);
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/unconfigured-entry-point"],
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // The readable half, the same shape `ModuleEndpointsTests` reads: `title` carries
        // `ConversationErrors`' own stable code (`Module.EntryPointNotConfigured`), and `detail` is this
        // handler's own message naming the missing module key - never a bare 503 the caller has to
        // guess the cause of.
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("Module.EntryPointNotConfigured", problem.Title);
        Assert.Contains(moduleKey, problem.Detail);

        var gateway = (RecordingModuleRegistrationGateway)host.Services.GetRequiredService<IModuleRegistrationGateway>();
        Assert.Empty(gateway.RegisterCalls);
    }

    /// <summary>`23-92`/`adr/0154`'s own headline claim, over the real HTTP pipeline: a request body
    /// carrying the field's old name, `entryPoint`, sent as raw JSON rather than through
    /// <see cref="OwnerModuleEndpoints.GrantModuleRequest"/> (which has no such property to serialize it
    /// from). The grant still succeeds - the field is silently ignored by minimal-API model binding,
    /// exactly as an unrecognised member always is - and the entry point the module-registration gateway
    /// actually receives is <see cref="ConfiguredEntryPoint"/>, this test host's own declared value,
    /// never the one the request body carried.</summary>
    [Fact]
    public async Task OwnerToken_Grants_IgnoresAnySmuggledEntryPointInTheRawBody_AndUsesTheConfiguredOne()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var rawBody = JsonContent.Create(new
        {
            moduleKey,
            triggerWords = new[] { "/smuggled-entry-point" },
            entryPoint = "https://an-attacker-supplied-entry-point.example.com",
            credential = "an-owner-minted-secret-of-sixteen-plus-chars",
            expiresAt = (DateTimeOffset?)null,
        });

        var response = await ownerClient.PutAsync(OwnerRoute, rawBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var gateway = (RecordingModuleRegistrationGateway)host.Services.GetRequiredService<IModuleRegistrationGateway>();
        var call = Assert.Single(gateway.RegisterCalls, c => c.Module.ModuleKey.Value == moduleKey);
        Assert.Equal(ConfiguredEntryPoint, call.Module.EntryPoint.ToString().TrimEnd('/'));
    }

    // ------------------------------------------------------------------------------------------
    // `23-83`/`adr/0151`: rotate and verify, on the platform owner's own behalf - the two writes
    // `22-11` never gave the owner, added only once the tenant's own copies stopped existing as
    // routes (`ModuleEndpointsTests`'s own fails-before proof that they are actually gone).
    // ------------------------------------------------------------------------------------------

    /// <summary>The item's own headline claim for rotate: a module the owner granted can have its
    /// credential rotated through the owner's own route, and the fresh value actually lands on both
    /// sides - Chat's own row and the (fake) module's own confirmation.</summary>
    [Fact]
    public async Task OwnerToken_RotatesACredential_AndTheStoredCredentialChanges()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/to-be-rotated"],
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });

        var response = await ownerClient.PostAsync($"{OwnerRoute}/{moduleKey}/rotate", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerModuleEndpoints.RotateModuleCredentialResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body.NewCredential));
        Assert.NotEqual("an-owner-minted-secret-of-sixteen-plus-chars", body.NewCredential);

        var gateway = (RecordingModuleRegistrationGateway)host.Services.GetRequiredService<IModuleRegistrationGateway>();
        var call = Assert.Single(gateway.RotateCalls, c => c.Module.ModuleKey.Value == moduleKey);
        Assert.Equal(ConfiguredProvisioningSecret, call.ProvisioningSecret.Value);
        Assert.Equal(body.NewCredential, call.NewCredential.Value);
    }

    [Fact]
    public async Task OwnerToken_RotatesAModuleThatIsNotEnabled_ReturnsNotFound()
    {
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsync($"{OwnerRoute}/{UniqueModuleKey()}/rotate", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>The identical wire-proof `OwnerToken_Grants_IgnoresAnySmuggledProvisioningSecretInTheRawBody_AndUsesTheConfiguredOne`
    /// gives for grant, restated for rotate: this route's own request carries no
    /// `ProvisioningSecret` field for a raw body to smuggle one into in the first place (there is no
    /// request body type at all - <see cref="OwnerModuleEndpoints.MapOwnerModuleEndpoints"/>'s own
    /// rotate route takes none), so the value the gateway receives can only ever be the one this host
    /// is configured with.</summary>
    [Fact]
    public async Task OwnerToken_Rotates_WhenThisDeploymentHasNoProvisioningSecretConfigured_Returns503_AndCallsNoGateway()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync(configureProvisioningSecret: false);
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsync($"{OwnerRoute}/{moduleKey}/rotate", content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var gateway = (RecordingModuleRegistrationGateway)host.Services.GetRequiredService<IModuleRegistrationGateway>();
        Assert.Empty(gateway.RotateCalls);
    }

    /// <summary>The item's own headline claim for verify: the reconciliation check runs on the
    /// owner's own behalf and reports agreement for a module both sides actually have.</summary>
    [Fact]
    public async Task OwnerToken_VerifiesARegistration_ReportsAgree()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync();
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        await ownerClient.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = moduleKey,
                TriggerWords = ["/to-be-verified"],
                Credential = "an-owner-minted-secret-of-sixteen-plus-chars",
                ExpiresAt = null,
            });

        var response = await ownerClient.PostAsJsonAsync(
            $"{OwnerRoute}/{moduleKey}/verify",
            new OwnerModuleEndpoints.VerifyModuleRegistrationAsOwnerRequest("https://calendar.example.com"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<OwnerModuleEndpoints.VerifyModuleRegistrationResponse>();
        Assert.NotNull(body);
        Assert.True(body.ChatHasRegistration);
        Assert.True(body.ModuleHasRegistration);
        Assert.True(body.Agree);

        var gateway = (RecordingModuleRegistrationGateway)host.Services.GetRequiredService<IModuleRegistrationGateway>();
        var call = Assert.Single(gateway.GetStatusCalls, c => c.Module.ModuleKey.Value == moduleKey);
        Assert.Equal(ConfiguredProvisioningSecret, call.ProvisioningSecret.Value);
    }

    [Fact]
    public async Task OwnerToken_Verifies_WhenThisDeploymentHasNoProvisioningSecretConfigured_Returns503_AndCallsNoGateway()
    {
        var moduleKey = UniqueModuleKey();
        await using var host = await BuildTestHostAsync(configureProvisioningSecret: false);
        var ownerClient = CreateClient(host, await fixture.GetPlatformOwnerAccessTokenAsync());

        var response = await ownerClient.PostAsJsonAsync(
            $"{OwnerRoute}/{moduleKey}/verify",
            new OwnerModuleEndpoints.VerifyModuleRegistrationAsOwnerRequest("https://calendar.example.com"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var gateway = (RecordingModuleRegistrationGateway)host.Services.GetRequiredService<IModuleRegistrationGateway>();
        Assert.Empty(gateway.GetStatusCalls);
    }

    [Fact]
    public async Task OrdinaryOperatorToken_CannotRotateACredential()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.PostAsync($"{OwnerRoute}/{UniqueModuleKey()}/rotate", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_CannotRotateACredential()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PostAsync($"{OwnerRoute}/{UniqueModuleKey()}/rotate", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OrdinaryOperatorToken_CannotVerifyARegistration()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.PostAsJsonAsync(
            $"{OwnerRoute}/{UniqueModuleKey()}/verify",
            new OwnerModuleEndpoints.VerifyModuleRegistrationAsOwnerRequest("https://calendar.example.com"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_CannotVerifyARegistration()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PostAsJsonAsync(
            $"{OwnerRoute}/{UniqueModuleKey()}/verify",
            new OwnerModuleEndpoints.VerifyModuleRegistrationAsOwnerRequest("https://calendar.example.com"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ------------------------------------------------------------------------------------------
    // The authorization boundary: this route is RequirePlatformOwner and nothing weaker.
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task OrdinaryOperatorToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoOperatorAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = UniqueModuleKey(),
                TriggerWords = ["/x"],
                Credential = "a-perfectly-valid-shaped-secret-value-x",
                ExpiresAt = null,
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>The case that matters most, the identical lesson `OwnerSitesEndpointTests` already
    /// proves for the read side: `5-08`'s site-wide `"Admin"`, holding `site:configure` for their own
    /// site, is still not the platform owner. A permission granted broadly inside one tenant does not
    /// become a cross-tenant write.</summary>
    [Fact]
    public async Task SiteConfigureHoldingAdminToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, await fixture.GetDemoAdminAccessTokenAsync());

        var response = await client.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = UniqueModuleKey(),
                TriggerWords = ["/x"],
                Credential = "a-perfectly-valid-shaped-secret-value-x",
                ExpiresAt = null,
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoToken_IsRejected()
    {
        await using var host = await BuildTestHostAsync();
        using var client = CreateClient(host, token: null);

        var response = await client.PutAsJsonAsync(
            OwnerRoute, new OwnerModuleEndpoints.GrantModuleRequest
            {
                ModuleKey = UniqueModuleKey(),
                TriggerWords = ["/x"],
                Credential = "a-perfectly-valid-shaped-secret-value-x",
                ExpiresAt = null,
            });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // `22-17`'s own "must not become the normal path" claim used to be checked here mechanically: a
    // platform-owner token, refused on the tenant's own self-service `PUT`, proved the two surfaces do
    // not blur into each other. `23-83`/`adr/0151` removed that `PUT` outright rather than moving it
    // behind a stronger check, so the question this test asked no longer has anywhere to be asked -
    // there is no write route left on the tenant's own side for an owner token (or anyone else's) to
    // reach. `ModuleEndpointsTests.NoToken_AlsoGetsNotFound_OnTheRemovedEnableRoute` is this file's
    // own replacement proof: the removed route answers `404` for every caller, platform owner
    // included, not merely `403` for the wrong kind of one.

    private async Task<ModuleEndpoints.EnabledModulesResponse> GetModulesAsync(HttpClient operatorClient)
    {
        var response = await operatorClient.GetAsync(SelfServiceRoute);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ModuleEndpoints.EnabledModulesResponse>();
        Assert.NotNull(body);
        return body;
    }

    /// <summary>
    /// `23-83`/`adr/0151`: several tests in this file need a `GrantedByOwner: false` row to compare an
    /// owner's own write against - "this is a tenant's own purchase, not an owner grant" is exactly
    /// what those tests exercise. Before this item that row came from a live `PUT` on the tenant's own
    /// self-service route; that route is gone, so the row is written directly, the identical shape
    /// <c>ModuleEndpointsTests.DemoAdminToken_CannotListAnotherTenantsEnabledModules</c> already uses
    /// for its own victim row. <see cref="EnabledModule"/>'s <c>grantedByOwner</c> parameter defaults
    /// to <see langword="false"/>, so simply not passing it is what makes this a self-service-shaped
    /// row rather than an owner-shaped one.
    /// </summary>
    private async Task SeedSelfServicePurchaseAsync(string moduleKey, string triggerWord)
    {
        await using var db = fixture.CreateDbContext();
        db.EnabledModules.Add(new EnabledModule(
            new EnabledModuleId(Guid.NewGuid()), fixture.SeededSiteId, new ModuleKey(moduleKey), [triggerWord],
            new Uri("https://faq.example.com"), new ModuleCredential("a-tenant-minted-secret-of-sixteen-plus-chars"),
            DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    /// <summary>Reads chat's own granted quantity straight off Postgres, one real
    /// <see cref="AgoChatDbContext"/> shared between the store and its outbox writer (unlike a call
    /// that only reads, <see cref="ModuleQuantityGrantStore.GetQuantityAsync"/> touches no outbox row,
    /// but the constructor still needs a writer to satisfy the type).</summary>
    private async Task<int> GetStoredQuantityAsync(string moduleKey)
    {
        await using var db = fixture.CreateDbContext();
        return await new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator())
            .GetQuantityAsync(fixture.SeededSiteId, new ModuleKey(moduleKey), CancellationToken.None);
    }

    private static string UniqueModuleKey() => $"owner-grant-{Guid.NewGuid():N}"[..24];

    private static HttpClient CreateClient(WebApplication host, string? token)
    {
        var client = host.GetTestClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    /// <summary>`23-65`/`adr/0150`: the value this test host's own `Ago.Chat.Api` is "deployed with" -
    /// distinct, deliberately, from every other secret-shaped literal this file already sends as
    /// caller input (e.g. `Credential`), so a test can prove the two are never confused. Never sent by
    /// any client in this file - the whole point of the wire-proof tests below.</summary>
    private const string ConfiguredProvisioningSecret = "the-deployments-own-configured-secret-value";

    /// <summary>`23-92`/`adr/0154`: the value this test host's own deployment "declares" for every
    /// module key - distinct from `Credential`'s own literal for the identical reason
    /// <see cref="ConfiguredProvisioningSecret"/> is distinct from it, so a test can prove the resolved
    /// entry point is the configured one, never anything a caller could smuggle in.</summary>
    private const string ConfiguredEntryPoint = "https://the-deployments-own-configured-entry-point.example.com";

    private async Task<WebApplication> BuildTestHostAsync(
        bool configureProvisioningSecret = true, bool configureEntryPoint = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddRouting();
        builder.Services.AddPlatformKernel();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<Npgsql.NpgsqlDataSource>()));

        builder.Services.AddScoped<IOperatorRepository, OperatorRepository>();
        builder.Services.AddScoped<ResolveOperatorIdentityHandler>();
        builder.Services.AddScoped<IPermissionChecker, PermissionChecker>();
        builder.Services.AddScoped<IEnabledModuleRepository, EnabledModuleRepository>();
        builder.Services.AddScoped<IEnabledModuleReadStore, EnabledModuleReadStore>();
        builder.Services.AddScoped<ISiteRepository, SiteRepository>();
        // `23-13`: a real repository, not a fake - this suite already runs against a real Postgres
        // (fixture.DataSource), and the whole point of the new tests below is proving a real row lands.
        builder.Services.AddScoped<IModuleRevokeOverrideRepository, ModuleRevokeOverrideRepository>();
        // `23-102`: EnableModuleForSiteAsOwnerHandler now also seeds a module's own permissions into
        // this site's roles - a real repository, the same "already runs against a real Postgres"
        // posture as IModuleRevokeOverrideRepository right above, not a fake, since this suite's own
        // fixture.SeededSiteId carries a real "Admin" role row this handler's own AddPermissionsAsync
        // call would otherwise have nothing to resolve DI against.
        builder.Services.AddScoped<IRoleRepository, RoleRepository>();
        // `23-59`: EnableModuleForSiteAsOwnerHandler now also requests this site's own retroactive
        // contact carry-over - a real repository, the same "already runs against a real Postgres"
        // posture as IRoleRepository right above, not a fake.
        builder.Services.AddScoped<IContactCarryoverRequestStore, ContactCarryoverRequestStore>();
        // A fake, not a real HTTP call - this suite is about the wire from operator/owner to
        // handler, not about whether a module deployment answers (the identical judgement
        // ModuleEndpointsTests's own remarks make for its sibling).
        builder.Services.AddSingleton<IModuleRegistrationGateway>(new RecordingModuleRegistrationGateway());
        builder.Services.AddSingleton<IClock, Ago.Platform.Hosting.SystemClock>();

        // `23-65`/`adr/0150`: the real `ChatModule.cs` wiring, reproduced here rather than resolved
        // through `AddChatModule` - the same "each integration suite wires exactly the handlers it
        // needs" posture every other registration on this page already follows. `configureProvisioningSecret`
        // lets a test build a host that has genuinely not been given a secret, the deployment state
        // `IModuleProvisioningSecretProvider`'s own remarks describe.
        builder.Services.AddSingleton(new ModuleProvisioningOptions
        {
            Secret = configureProvisioningSecret ? ConfiguredProvisioningSecret : string.Empty,
        });
        builder.Services.AddSingleton<IModuleProvisioningSecretProvider, ConfiguredModuleProvisioningSecretProvider>();

        // `23-92`/`adr/0154`: the identical shape, for the module's own entry point.
        // `AnyKeyModuleEntryPointProvider` rather than the real `ConfiguredModuleEntryPointProvider`
        // bound to a config section - this suite's own module keys are randomly generated per test
        // (`UniqueModuleKey()`), so a section keyed by one literal module name would not exercise the
        // real HTTP-to-handler wire this file proves for every other field; the config-parsing half is
        // covered where it lives (`Ago.Chat.Application.Tests`' own `FakeModuleEntryPointProvider`, and
        // `ConfiguredModuleEntryPointProvider`'s own unit coverage).
        builder.Services.AddSingleton<IModuleEntryPointProvider>(
            new AnyKeyModuleEntryPointProvider(configureEntryPoint ? new Uri(ConfiguredEntryPoint) : null));

        // `23-102`: the identical "any key resolves the same fixed answer" shape as
        // AnyKeyModuleEntryPointProvider right above, for the module's own permission set.
        // ModulePermissionSet.Empty - not a real ModulePermissionSet - deliberately: this suite's
        // fixture.SeededSiteId's "Admin" role is shared, real Postgres state across every test in this
        // collection (OperatorOidcFixture.InitializeAsync's own remarks), so a non-empty default here
        // would have one test's grant silently widen every later test's "Admin" role - the exact
        // cross-test pollution this suite's own real-repository choices elsewhere are careful to avoid.
        // The seeding behaviour itself (a non-empty ModulePermissionSet actually landing in the role)
        // is proven in isolation by RoleRepositoryTests and at the Application level by
        // EnableModuleForSiteAsOwnerHandlerTests - this file's own job is the HTTP-to-handler wire, not
        // re-proving that.
        builder.Services.AddSingleton<IModulePermissionsProvider>(new AnyKeyModulePermissionsProvider(ModulePermissionSet.Empty));

        // `23-83`/`adr/0151`: the tenant's own self-service handlers that used to be registered here
        // (EnableModuleForSiteHandler/RotateModuleCredentialHandler/RevokeModuleForSiteHandler/
        // VerifyModuleRegistrationHandler) are gone - `Api.Modules.ModuleEndpoints` maps only the GET
        // route now, and that handler is the one still registered below.
        // `23-01`: MapModuleEndpoints maps the whole self-service group at once, including
        // the GET route's own handler - an unregistered handler here fails endpoint
        // construction for the group as a whole (ModuleEndpointsTests's own remarks), which
        // is exactly what this file's own fails-before run demonstrated.
        builder.Services.AddScoped<ListEnabledModulesForSiteHandler>();
        builder.Services.AddScoped<EnableModuleForSiteAsOwnerHandler>();
        builder.Services.AddScoped<RevokeModuleForSiteAsOwnerHandler>();
        // `23-83`/`adr/0151`: the platform owner's own rotate/verify, added once the tenant's own
        // copies stopped existing as routes. The real generator, not a fake - it wraps
        // RandomNumberGenerator with no external dependency, the identical reasoning
        // `ChatModule.cs`'s own production registration already gives for using it unfaked.
        builder.Services.AddSingleton<IModuleCredentialGenerator, ModuleCredentialGenerator>();
        builder.Services.AddScoped<RotateModuleCredentialAsOwnerHandler>();
        builder.Services.AddScoped<VerifyModuleRegistrationAsOwnerHandler>();
        // `23-66`: the platform owner's own quantity grant, alongside the pair above - real Postgres
        // store, real outbox writer, the same "this suite already runs against a real Postgres" posture
        // ModuleQuantityGrantedOutboxTests already established for this exact store.
        builder.Services.AddScoped<IModuleQuantityGrantStore, ModuleQuantityGrantStore>();
        builder.Services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        builder.Services.AddScoped<GrantModuleQuantityAsOwnerHandler>();
        // `24-12`: the owner endpoint's own access-record write - OwnerAccessRecorder resolves this
        // straight from DI, the same way the production host does. IClock/IIdGenerator are already
        // registered above (AddPlatformKernel).
        builder.Services.AddScoped<IAccessRecordRepository, AccessRecordRepository>();

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IClaimsTransformation, OperatorIdentityClaimsTransformation>();

        builder.Services.AddAuthentication()
            .AddJwtBearer(JwtSchemes.Operator, options =>
            {
                options.MapInboundClaims = false;
                options.Authority = fixture.KeycloakAuthority;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidAudience = OperatorOidcFixture.ClientId,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };
            });
        builder.Services.AddAuthorization(options =>
        {
            // `Program.cs`'s own declarations, reproduced verbatim - both policies, on the same host,
            // because this file proves the boundary between them, not just one side of it.
            options.AddPolicy("RequireOperatorIdentity", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireClaim(AgoClaimTypes.OperatorId));
            options.AddPolicy("RequirePlatformOwner", policy => policy
                .AddAuthenticationSchemes(JwtSchemes.Operator)
                .RequireAuthenticatedUser()
                .AddRequirements(new PlatformOwnerRequirement()));
        });
        builder.Services.AddSingleton<IAuthorizationHandler, PlatformOwnerAuthorizationHandler>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapModuleEndpoints();
        app.MapOwnerModuleEndpoints();

        await app.StartAsync();
        return app;
    }

    /// <summary>Always succeeds - the identical fake-gateway shape <c>ModuleEndpointsTests</c>'s own
    /// private nested class already establishes for its sibling suite (not shared as a type, since
    /// each integration suite keeps its own minimal double rather than a shared test-only dependency).
    /// `23-65`: records every call's <see cref="ModuleProvisioningSecret"/>, unlike its predecessor
    /// (`AlwaysSucceedsModuleRegistrationGateway`, renamed) - the wire-proof tests below need to see
    /// which secret actually reached the module-registration boundary, not merely that the call
    /// succeeded.</summary>
    /// <summary>`23-92`/`adr/0154`: resolves every module key to the identical configured entry point
    /// (or none, when built with a <see langword="null"/> value) - see <see cref="BuildTestHostAsync"/>'s
    /// own remarks for why this stands in for the real <c>ConfiguredModuleEntryPointProvider</c> in this
    /// suite.</summary>
    private sealed class AnyKeyModuleEntryPointProvider(Uri? entryPoint) : IModuleEntryPointProvider
    {
        public Uri? TryGet(ModuleKey moduleKey) => entryPoint;
    }

    /// <summary>`23-102`: resolves every module key to the identical configured
    /// <see cref="ModulePermissionSet"/> - the same "stands in for the real
    /// ConfiguredModulePermissionsProvider" role <see cref="AnyKeyModuleEntryPointProvider"/> already
    /// plays for its sibling port, for the identical reason (this suite's module keys are randomly
    /// generated per test, so a config section keyed by one literal name would not exercise anything).</summary>
    private sealed class AnyKeyModulePermissionsProvider(ModulePermissionSet permissions) : IModulePermissionsProvider
    {
        public ModulePermissionSet Get(ModuleKey moduleKey) => permissions;
    }

    private sealed class RecordingModuleRegistrationGateway : IModuleRegistrationGateway
    {
        public List<(ModuleRegistrationTarget Module, ModuleProvisioningSecret ProvisioningSecret)> RegisterCalls { get; } = [];

        public List<(ModuleRegistrationTarget Module, ModuleProvisioningSecret ProvisioningSecret)> RevokeCalls { get; } = [];

        // `23-83`: the owner's own rotate/verify need the identical wire-proof shape RegisterCalls/
        // RevokeCalls already give - which secret actually reached the module-registration boundary,
        // not merely that the call succeeded.
        public List<(ModuleRegistrationTarget Module, ModuleCredential NewCredential, ModuleProvisioningSecret ProvisioningSecret)> RotateCalls { get; } = [];

        public List<(ModuleRegistrationTarget Module, ModuleProvisioningSecret ProvisioningSecret)> GetStatusCalls { get; } = [];

        public Task RegisterAsync(
            ModuleRegistrationTarget module, ModuleCredential credential, ModuleProvisioningSecret provisioningSecret,
            string displayName, CancellationToken cancellationToken)
        {
            RegisterCalls.Add((module, provisioningSecret));
            return Task.CompletedTask;
        }

        public Task RotateAsync(
            ModuleRegistrationTarget module, ModuleCredential newCredential, ModuleProvisioningSecret provisioningSecret,
            CancellationToken cancellationToken)
        {
            RotateCalls.Add((module, newCredential, provisioningSecret));
            return Task.CompletedTask;
        }

        public Task RevokeAsync(
            ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken)
        {
            RevokeCalls.Add((module, provisioningSecret));
            return Task.CompletedTask;
        }

        public Task<ModuleRegistrationRemoteStatus> GetStatusAsync(
            ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken)
        {
            GetStatusCalls.Add((module, provisioningSecret));
            return Task.FromResult(new ModuleRegistrationRemoteStatus(Exists: true, DateTimeOffset.UtcNow, HasCredentialInGracePeriod: false));
        }

        // `22-30`: this suite never exercises erasure - added only so this always-succeeds double
        // still compiles against the interface, the identical minimal-stub treatment every other
        // never-exercised method on this class would get if one existed before this item.
        public Task<TenantDataErasureResult> EraseTenantDataAsync(
            ModuleRegistrationTarget module, ModuleProvisioningSecret provisioningSecret, CancellationToken cancellationToken) =>
            Task.FromResult(new TenantDataErasureResult(TenantExisted: true, Confirmed: true));
    }
}
