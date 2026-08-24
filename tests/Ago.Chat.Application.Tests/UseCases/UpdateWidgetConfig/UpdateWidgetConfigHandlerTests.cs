using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.UpdateWidgetConfig;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UpdateWidgetConfig;

public class UpdateWidgetConfigHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        UpdateWidgetConfigHandler Handler, FakeSiteRepository Sites, FakeOutboxWriter Outbox);

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
        var handler = new UpdateWidgetConfigHandler(
            sites, permissions, outbox, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, sites, outbox);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_UpdatesTheSitesWidgetConfig()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateWidgetConfig.UpdateWidgetConfig(
                SiteId, OperatorId, "#abcdef", nameof(Position.BottomLeft)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("#abcdef", result.Value.PrimaryColorHex);
        Assert.Equal(Position.BottomLeft, result.Value.Position);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal("#abcdef", saved!.WidgetConfig.PrimaryColorHex);
        Assert.Equal(Position.BottomLeft, saved.WidgetConfig.Position);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_EnqueuesExactlyOneSiteSettingsChangedEnvelope()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateWidgetConfig.UpdateWidgetConfig(
                SiteId, OperatorId, null, nameof(Position.BottomRight)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(SiteSettingsChanged), envelope.Type);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_ClearsTheSitesDomainEventsAfterEnqueuing()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateWidgetConfig.UpdateWidgetConfig(
                SiteId, OperatorId, null, nameof(Position.BottomLeft)),
            CancellationToken.None);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Empty(saved!.DomainEvents);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteConfigure_ReturnsForbidden_AndEnqueuesNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateWidgetConfig.UpdateWidgetConfig(
                SiteId, OperatorId, "#abcdef", nameof(Position.BottomLeft)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSiteDoesNotExist_ReturnsSiteNotFound()
    {
        var sites = new FakeSiteRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.SiteConfigure);
        var handler = new UpdateWidgetConfigHandler(
            sites, permissions, new FakeOutboxWriter(), new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.UpdateWidgetConfig.UpdateWidgetConfig(
                SiteId, OperatorId, "#abcdef", nameof(Position.BottomLeft)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.NotFound", result.Error!.Value.Code);
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("#fff")]
    [InlineData("#gggggg")]
    public async Task HandleAsync_WhenTheColorIsMalformedHex_ReturnsInvalidColor(string malformedHex)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateWidgetConfig.UpdateWidgetConfig(
                SiteId, OperatorId, malformedHex, nameof(Position.BottomLeft)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidColor", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenThePositionIsNotARecognizedValue_ReturnsInvalidPosition()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateWidgetConfig.UpdateWidgetConfig(SiteId, OperatorId, null, "diagonal"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("WidgetConfig.InvalidPosition", result.Error!.Value.Code);
    }
}
