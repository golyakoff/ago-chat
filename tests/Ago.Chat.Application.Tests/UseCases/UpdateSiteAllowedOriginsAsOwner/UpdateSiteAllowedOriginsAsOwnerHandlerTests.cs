using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.UpdateSiteAllowedOriginsAsOwner;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UpdateSiteAllowedOriginsAsOwner;

/// <summary>
/// `23-48`'s own Done-when at the Application level: a tenant's site address can be changed without
/// anyone writing SQL, a value that is not an origin is refused naming what is wrong with it, and the
/// write enqueues both cache-invalidation contracts the two cache shapes need
/// (<see cref="SiteSettingsChanged"/> for the site-config cache,
/// <see cref="SiteAllowedOriginsChanged"/> for the new CORS-layer one - proven end to end against real
/// infrastructure in <c>SiteAllowedOriginsCacheInvalidationEndToEndTests</c>,
/// <c>Ago.Chat.Integration.Tests</c>).
///
/// <para>This handler takes no <c>OperatorId</c>/<c>RequestedBy</c> and calls no
/// <see cref="Application.Abstractions.IPermissionChecker"/> at all - the constructor signature below
/// is itself the fails-before proof that the sole gate is the route's own <c>RequirePlatformOwner</c>
/// policy, not a second, weaker copy of it here (the same shape
/// <c>EnableModuleForSiteAsOwnerHandlerTests</c>' own class remarks state for its sibling).</para>
/// </summary>
public class UpdateSiteAllowedOriginsAsOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    private sealed record Fixture(
        UpdateSiteAllowedOriginsAsOwnerHandler Handler, FakeSiteRepository Sites, FakeOutboxWriter Outbox);

    private static Fixture CreateFixture(IReadOnlyList<string>? initialOrigins = null)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", initialOrigins ?? ["https://old.example"], name: "Old Barbershop"));
        var outbox = new FakeOutboxWriter();
        var handler = new UpdateSiteAllowedOriginsAsOwnerHandler(sites, outbox, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, sites, outbox);
    }

    private static Application.UseCases.UpdateSiteAllowedOriginsAsOwner.UpdateSiteAllowedOriginsAsOwner Command(
        params string[] origins) => new(SiteId, origins);

    [Fact]
    public async Task HandleAsync_WithValidOrigins_ReplacesTheSitesAllowedOrigins()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command("https://new.example", "https://second.example"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["https://new.example", "https://second.example"], result.Value);
        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(["https://new.example", "https://second.example"], saved!.AllowedOrigins);
    }

    [Fact]
    public async Task HandleAsync_WithDuplicateOrigins_DeduplicatesBeforeSaving()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command("https://new.example", "https://new.example"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(["https://new.example"], result.Value);
    }

    /// <summary>Fails-before: before this guard existed, an empty list would have reached
    /// <see cref="Site.UpdateAllowedOrigins"/> and been saved verbatim, silently locking the widget out
    /// of every page on that tenant's site - the exact "a save that appears to work and takes effect
    /// an hour later is worse than one that refuses" failure `23-48`'s own brief names, taken to its
    /// limit (a config that can never work at all, not merely a delayed one).</summary>
    [Fact]
    public async Task HandleAsync_WithAnEmptyList_ReturnsInvalidOrigin_AndSavesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.InvalidOrigin", result.Error!.Value.Code);
        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(["https://old.example"], saved!.AllowedOrigins);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    /// <summary>Fails-before: before OriginValidator was called from this handler, a value with a path
    /// would have been stored verbatim and then never matched a real browser's `Origin` header, which
    /// carries no path at all - the widget would connect from nowhere, silently, on a screen that
    /// otherwise looks fully configured (this item's own "Why it costs more than it looks").</summary>
    [Theory]
    [InlineData("https://shop.example/booking")]
    [InlineData("not-a-url")]
    [InlineData("ftp://shop.example")]
    [InlineData("https://shop.example/")]
    public async Task HandleAsync_WithAMalformedOrigin_ReturnsInvalidOrigin_AndSavesNothing(string malformed)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command("https://good.example", malformed), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.InvalidOrigin", result.Error!.Value.Code);
        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(["https://old.example"], saved!.AllowedOrigins);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_ForASiteThatDoesNotExist_ReturnsSiteNotFound()
    {
        var fixture = CreateFixture();
        var missingSiteId = new SiteId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateSiteAllowedOriginsAsOwner.UpdateSiteAllowedOriginsAsOwner(
                missingSiteId, ["https://new.example"]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }

    /// <summary>The write's whole point: two cache shapes, two contracts, both enqueued in the same
    /// call - see this handler's own remarks for why one is not enough
    /// (`SiteSettingsChanged` for the site-config cache, `SiteAllowedOriginsChanged` for the CORS-layer
    /// one no existing consumer ever touched before this item).</summary>
    [Fact]
    public async Task HandleAsync_WhenSuccessful_EnqueuesBothCacheInvalidationContracts()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command("https://new.example"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, fixture.Outbox.Enqueued.Count);
        Assert.Contains(fixture.Outbox.Enqueued, e => e.Type == nameof(SiteSettingsChanged));
        Assert.Contains(fixture.Outbox.Enqueued, e => e.Type == nameof(SiteAllowedOriginsChanged));
    }

    [Fact]
    public async Task HandleAsync_WhenSuccessful_TheAllowedOriginsChangedEnvelopeCarriesBothLists()
    {
        var fixture = CreateFixture(["https://old.example", "https://also-old.example"]);

        await fixture.Handler.HandleAsync(Command("https://new.example"), CancellationToken.None);

        var envelope = fixture.Outbox.Enqueued.Single(e => e.Type == nameof(SiteAllowedOriginsChanged));
        var payload = System.Text.Json.JsonSerializer.Deserialize<SiteAllowedOriginsChanged>(envelope.Payload)!;
        Assert.Equal(["https://old.example", "https://also-old.example"], payload.PreviousOrigins);
        Assert.Equal(["https://new.example"], payload.AllowedOrigins);
    }
}
