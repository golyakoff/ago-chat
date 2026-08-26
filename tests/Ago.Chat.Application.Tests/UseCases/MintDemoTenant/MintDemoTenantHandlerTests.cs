using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.MintDemoTenant;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Tests.UseCases.MintDemoTenant;

/// <summary>
/// `8-07`: the minting decisions, all of which happen before anything touches Postgres or Keycloak and
/// none of which need either to be exercised.
///
/// <para>The two that matter most are the guards. `8-07` requires the endpoint to be "rate-limited per
/// IP with the existing limiter, and capped in total" - <b>both, not either</b> - and asks for the cap
/// to be a correctness property with a test rather than a config value nobody exercises. These are that
/// test.</para>
/// </summary>
public class MintDemoTenantHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private sealed record Harness(
        MintDemoTenantHandler Handler,
        FakeDemoTenantRepository DemoTenants,
        FakeSiteRegistrationRepository Registrations,
        FakeDemoIdentityProvisioner Identities);

    private static Harness CreateHandler(
        DemoTenantOptions? options = null, IRateLimiter? rateLimiter = null)
    {
        var demoTenants = new FakeDemoTenantRepository();
        var registrations = new FakeSiteRegistrationRepository();
        var identities = new FakeDemoIdentityProvisioner();

        var handler = new MintDemoTenantHandler(
            demoTenants,
            registrations,
            identities,
            new FakeDemoCredentialGenerator(),
            rateLimiter ?? new FakeRateLimiter(),
            options ?? new DemoTenantOptions { Enabled = true, VisitorOrigin = "https://demo.example" },
            new DemoTenantRateLimitOptions(),
            new FakeIdGenerator(),
            new FakeClock(Now));

        return new Harness(handler, demoTenants, registrations, identities);
    }

    private static Task<Result<MintedDemoTenant>> MintAsync(Harness harness, string ip = "203.0.113.7") =>
        harness.Handler.HandleAsync(new global::Ago.Chat.Application.UseCases.MintDemoTenant.MintDemoTenant(ip), CancellationToken.None);

    // ---------------------------------------------------------------------------------------------
    // The happy path, and what "recognisably temporary" means concretely
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ItMintsATenantWithCredentials_AnExpiry_AndTheDemoOriginAllowed()
    {
        var harness = CreateHandler(new DemoTenantOptions
        {
            Enabled = true,
            VisitorOrigin = "https://demo.example",
            Lifetime = TimeSpan.FromHours(24),
        });

        var result = await MintAsync(harness);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.AddHours(24), result.Value.ExpiresAt);
        Assert.Equal("fake-demo-password", result.Value.Password);
        Assert.StartsWith("demo-", result.Value.Username, StringComparison.Ordinal);

        var registration = Assert.Single(harness.Registrations.Registered);
        // The one column that makes this a demo tenant, and the reason the sweeper will ever find it.
        Assert.Equal(Now.AddHours(24), registration.Site.DemoExpiresAt);
        Assert.True(registration.Site.IsDemo);
        // Without the demo page's origin on the new site, the minted tenant's widget would be refused
        // by `5-01`'s layer 2 and the console would stay empty forever.
        Assert.Equal(["https://demo.example"], registration.Site.AllowedOrigins);
    }

    /// <summary>
    /// `8-07`'s Scope: "Everything minted is recognisably temporary: names, the on-screen credentials,
    /// and whatever the owner view (`12-03`) shows, so a demo tenant is never mistaken for a real
    /// customer." The owner view renders <c>sites.name</c>, so putting the word and the expiry there is
    /// what makes that true without `12-03` having to change.
    /// </summary>
    [Fact]
    public async Task TheSiteNameAndPublicKeySayItIsATemporaryDemo()
    {
        var harness = CreateHandler();

        var result = await MintAsync(harness);

        Assert.Contains("Demo tenant", result.Value.SiteName, StringComparison.Ordinal);
        Assert.Contains("expires", result.Value.SiteName, StringComparison.Ordinal);
        Assert.StartsWith("demo_", result.Value.SitePublicKey, StringComparison.Ordinal);
        Assert.Equal(result.Value.SiteName, Assert.Single(harness.Registrations.Registered).Site.Name);
    }

    /// <summary>The link a viewer opens to be a visitor of their own tenant. Without it a per-viewer
    /// tenant is an empty console, which would be worse than the shared account it replaces
    /// (`adr/0058`).</summary>
    [Fact]
    public async Task TheVisitorUrlCarriesThisTenantsOwnPublicKey()
    {
        var harness = CreateHandler();

        var result = await MintAsync(harness);

        Assert.Equal($"https://demo.example/?site={result.Value.SitePublicKey}", result.Value.VisitorUrl);
    }

    // ---------------------------------------------------------------------------------------------
    // The two guards - `8-07` requires both
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The total cap. This is the half a per-IP limiter cannot do: a thousand callers each politely
    /// minting one tenant passes every rate limit ever written.
    /// </summary>
    [Fact]
    public async Task WhenTheCapIsReached_ItRefusesAndWritesNothing()
    {
        var harness = CreateHandler(new DemoTenantOptions
        {
            Enabled = true,
            VisitorOrigin = "https://demo.example",
            MaxLiveTenants = 3,
        });
        harness.DemoTenants.LiveCount = 3;

        var result = await MintAsync(harness);

        Assert.True(result.IsFailure);
        Assert.Equal("demo.capacity_reached", result.Error!.Value.Code);
        Assert.Empty(harness.Registrations.Registered);
        Assert.Empty(harness.Identities.Created);
    }

    /// <summary>
    /// The boundary, both sides of it, in one test - because an off-by-one in a cap is the whole bug.
    /// One below the cap must succeed, or the cap is really <c>MaxLiveTenants - 1</c>.
    /// </summary>
    [Fact]
    public async Task TheCapIsInclusive_OneBelowItStillMints()
    {
        var options = new DemoTenantOptions
        {
            Enabled = true,
            VisitorOrigin = "https://demo.example",
            MaxLiveTenants = 3,
        };

        var below = CreateHandler(options);
        below.DemoTenants.LiveCount = 2;
        Assert.True((await MintAsync(below)).IsSuccess);

        var at = CreateHandler(options);
        at.DemoTenants.LiveCount = 3;
        Assert.True((await MintAsync(at)).IsFailure);
    }

    [Fact]
    public async Task WhenThePerIpLimitDenies_ItRefusesBeforeAnyDatabaseWork()
    {
        var harness = CreateHandler(rateLimiter: new RateLimitedFakeRateLimiter(TimeSpan.FromMinutes(5)));

        var result = await MintAsync(harness);

        Assert.True(result.IsFailure);
        Assert.Equal("demo.rate_limited", result.Error!.Value.Code);
        Assert.Empty(harness.Registrations.Registered);
        // The cap was never even read: a caller who was going to be turned away should not cost a query.
        Assert.Equal(0, harness.DemoTenants.LiveCount);
    }

    // ---------------------------------------------------------------------------------------------
    // Enablement and failure ordering
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Off by default. An endpoint that creates tenants from the public internet must not be something a
    /// deployment acquires by upgrading - `8-07`'s Out of scope is explicit that this must not become a
    /// second registration path for real customers.
    /// </summary>
    [Fact]
    public async Task WhenDisabled_ItRefusesEverything()
    {
        var harness = CreateHandler(new DemoTenantOptions { VisitorOrigin = "https://demo.example" });

        var result = await MintAsync(harness);

        Assert.True(result.IsFailure);
        Assert.Equal("demo.disabled", result.Error!.Value.Code);
        Assert.Empty(harness.Registrations.Registered);
    }

    /// <summary>
    /// <b>The ordering, made observable.</b> Keycloak assigns the subject id and refuses one the caller
    /// chose (measured in `DemoTenantLifecycleTests`), so the identity has to be created before the
    /// operator row that names it. When the identity is refused, nothing is written at all - which is
    /// the good half of that order.
    /// </summary>
    [Fact]
    public async Task WhenTheIdentityProviderRefuses_NothingIsWritten()
    {
        var harness = CreateHandler();
        harness.Identities.RefuseWith = DemoTenantErrors.IdentityRejected("409");

        var result = await MintAsync(harness);

        Assert.True(result.IsFailure);
        Assert.Equal("demo.identity_rejected", result.Error!.Value.Code);
        Assert.Empty(harness.Registrations.Registered);
    }

    /// <summary>
    /// <b>The compensation, and the reason it has to exist.</b> The identity is created first, so a
    /// failed registration would otherwise leave a Keycloak user no site points at - and the expiry
    /// sweeper works from `sites`, so an identity with no site is invisible to it forever. This is the
    /// one leak the handler cannot order its way out of, and deleting the user it just made is what
    /// closes every case except a process death between the two writes (`adr/0058`).
    /// </summary>
    [Fact]
    public async Task WhenTheRegistrationFails_TheIdentityItJustCreatedIsDeleted()
    {
        var harness = CreateHandler();
        harness.Registrations.DenyNextRegistration = true;

        var result = await MintAsync(harness);

        Assert.True(result.IsFailure);
        Assert.Equal("demo.unavailable", result.Error!.Value.Code);
        var created = Assert.Single(harness.Identities.Created);
        Assert.Equal([created.SubjectId], harness.Identities.Deleted);
    }

    /// <summary>The subject id the operator row carries and the one handed to the identity provider are
    /// the same value - the property that lets the two writes happen in either order without a lookup,
    /// and the thing that would silently break if either side started generating its own.</summary>
    [Fact]
    public async Task TheOperatorRowAndTheIdentityShareOneSubjectId()
    {
        var harness = CreateHandler();

        await MintAsync(harness);

        var registration = Assert.Single(harness.Registrations.Registered);
        var identity = Assert.Single(harness.Identities.Created);
        Assert.Equal(registration.Operator.ExternalSubjectId, identity.SubjectId);
        Assert.Equal("fake-demo-password", identity.Password);
    }

    /// <summary>A minted operator gets both seeded roles. Half a console is a worse demonstration than
    /// none, and `10-02`'s own registration does exactly the same for a real tenant - which is the point:
    /// a demo tenant is structurally an ordinary one.</summary>
    [Fact]
    public async Task TheMintedOperatorHoldsBothSeededRoles()
    {
        var harness = CreateHandler();

        await MintAsync(harness);

        var registration = Assert.Single(harness.Registrations.Registered);
        Assert.Equal("Operator", registration.OperatorRole.Name);
        Assert.Equal("Admin", registration.AdminRole.Name);
        Assert.Contains("conversation:read", registration.OperatorRole.Permissions);
        Assert.Contains("site:configure", registration.AdminRole.Permissions);
    }
}
