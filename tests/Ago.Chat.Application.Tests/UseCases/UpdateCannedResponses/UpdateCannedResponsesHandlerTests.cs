using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetCannedResponses;
using Ago.Chat.Application.UseCases.UpdateCannedResponses;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UpdateCannedResponses;

/// <summary>`18-03`: the console's own read/write pair - the `site:configure` gate reused from
/// `14-04`/`11-01` rather than reinvented, and the deliberate absence of any outbox write
/// (<c>UpdateCannedResponsesHandler</c>'s own remarks, and <c>Site.UpdateCannedResponses</c>'s
/// beneath it, for why - nothing caches this value, so nothing needs telling).</summary>
public class UpdateCannedResponsesHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private sealed record Fixture(
        UpdateCannedResponsesHandler Handler,
        GetCannedResponsesHandler Reader,
        FakeSiteRepository Sites,
        FakeOutboxWriter Outbox);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        }

        var outbox = new FakeOutboxWriter();
        return new Fixture(
            new UpdateCannedResponsesHandler(sites, permissions),
            new GetCannedResponsesHandler(sites, permissions),
            sites,
            outbox);
    }

    private static Application.UseCases.UpdateCannedResponses.UpdateCannedResponses Command(
        IReadOnlyList<UpdateCannedResponsesItem>? responses = null) =>
        new(SiteId, OperatorId, responses ?? [new UpdateCannedResponsesItem("Refund policy", "Three working days.")]);

    [Fact]
    public async Task HandleAsync_WhenPermitted_StoresTheResponses()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        var response = Assert.Single(saved!.CannedResponses);
        Assert.Equal("Refund policy", response.Title);
        Assert.Equal("Three working days.", response.Body);
    }

    [Fact]
    public async Task ANewSite_HasNoCannedResponses()
    {
        var fixture = CreateFixture();

        var result = await fixture.Reader.HandleAsync(
            new GetCannedResponses(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_WritesNothingToTheOutbox()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        // The architectural decision this pair makes, proven rather than only documented - see
        // UpdateCannedResponsesHandler's own remarks on why there is no SiteSettingsChanged producer
        // here the way there is for widget config / offline auto-reply.
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigure_IsForbidden_AndWritesNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        var site = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Empty(site!.CannedResponses);
    }

    [Fact]
    public async Task GetAsync_WithoutSiteConfigure_IsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Reader.HandleAsync(
            new GetCannedResponses(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithAnEmptyTitle_IsARejectionRatherThanAThrow()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command([new UpdateCannedResponsesItem("", "Three working days.")]), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("CannedResponse.Invalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithAnEmptyBody_IsARejectionRatherThanAThrow()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command([new UpdateCannedResponsesItem("Refund policy", "")]), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("CannedResponse.Invalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithTooManyResponses_IsARejectionRatherThanAThrow()
    {
        var fixture = CreateFixture();
        var tooMany = Enumerable
            .Range(0, CannedResponse.MaxCount + 1)
            .Select(i => new UpdateCannedResponsesItem($"Title {i}", "Reply text."))
            .ToList();

        var result = await fixture.Handler.HandleAsync(Command(tooMany), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("CannedResponse.Invalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ReplacesThePreviousList()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(Command([new UpdateCannedResponsesItem("Greeting", "Hi there.")]), CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            Command([new UpdateCannedResponsesItem("Refund policy", "Three days.")]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        var only = Assert.Single(saved!.CannedResponses);
        Assert.Equal("Refund policy", only.Title);
    }
}
