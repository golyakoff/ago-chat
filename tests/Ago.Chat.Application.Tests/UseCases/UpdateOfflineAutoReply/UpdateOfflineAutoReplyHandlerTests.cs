using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetOfflineAutoReply;
using Ago.Chat.Application.UseCases.UpdateOfflineAutoReply;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UpdateOfflineAutoReply;

/// <summary>`14-04`: the console's own read/write pair - the `site:configure` gate the item requires
/// be reused rather than reinvented, and the <c>SiteSettingsChanged</c> outbox row that is what makes
/// the toggle live rather than TTL-delayed.</summary>
public class UpdateOfflineAutoReplyHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        UpdateOfflineAutoReplyHandler Handler,
        GetOfflineAutoReplyHandler Reader,
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
            new UpdateOfflineAutoReplyHandler(sites, permissions, outbox, new FakeIdGenerator(), new FakeClock(Now)),
            new GetOfflineAutoReplyHandler(sites, permissions),
            sites,
            outbox);
    }

    private static Application.UseCases.UpdateOfflineAutoReply.UpdateOfflineAutoReply Command(
        bool enabled = true,
        string fallback = "We are closed.",
        IReadOnlyList<UpdateOfflineAutoReplyRule>? rules = null) =>
        new(SiteId, OperatorId, enabled, fallback, rules ?? []);

    [Fact]
    public async Task HandleAsync_WhenPermitted_StoresTheScript()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(rules: [new UpdateOfflineAutoReplyRule("refund", "Three working days.")]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.True(saved!.OfflineAutoReply.Enabled);
        Assert.Equal("We are closed.", saved.OfflineAutoReply.FallbackReply);
        var rule = Assert.Single(saved.OfflineAutoReply.Rules);
        Assert.Equal("refund", rule.Keyword);
    }

    [Fact]
    public async Task ANewSite_HasTheAutoReplyOff()
    {
        var fixture = CreateFixture();

        var result = await fixture.Reader.HandleAsync(
            new GetOfflineAutoReply(SiteId, OperatorId), CancellationToken.None);

        // "Off by default... not a silent behaviour change to existing tenants' widgets" (`14-04`).
        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Enabled);
        Assert.Empty(result.Value.Rules);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_EnqueuesExactlyOneSiteSettingsChangedEnvelope()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(SiteSettingsChanged), envelope.Type);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigure_IsForbidden_AndWritesNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
        var site = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.False(site!.OfflineAutoReply.Enabled);
    }

    [Fact]
    public async Task GetAsync_WithoutSiteConfigure_IsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Reader.HandleAsync(
            new GetOfflineAutoReply(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_EnabledWithNoFallback_IsARejectionRatherThanAThrow()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(fallback: "   "), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OfflineAutoReply.Invalid", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WithAnEmptyRuleKeyword_IsARejectionRatherThanAThrow()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(rules: [new UpdateOfflineAutoReplyRule("", "Three working days.")]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OfflineAutoReply.Invalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_TurningItOffAgain_IsAllowedWithoutAFallback()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            Command(enabled: false, fallback: ""), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.False(saved!.OfflineAutoReply.Enabled);
    }
}
