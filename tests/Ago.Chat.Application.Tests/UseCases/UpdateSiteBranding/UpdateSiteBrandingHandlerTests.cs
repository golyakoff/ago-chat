using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.UpdateSiteBranding;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UpdateSiteBranding;

public class UpdateSiteBrandingHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(UpdateSiteBrandingHandler Handler, FakeSiteRepository Sites, FakeOutboxWriter Outbox);

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
        var handler = new UpdateSiteBrandingHandler(sites, permissions, outbox, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, sites, outbox);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_SetsTheBrandCompanyName()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateSiteBranding.UpdateSiteBranding(SiteId, OperatorId, "Acme Repairs LLC"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Acme Repairs LLC", result.Value);

        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal("Acme Repairs LLC", saved!.BrandCompanyName);
        Assert.Single(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WithABlankName_ClearsItToNull()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateSiteBranding.UpdateSiteBranding(SiteId, OperatorId, "Acme Repairs LLC"), CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateSiteBranding.UpdateSiteBranding(SiteId, OperatorId, "   "), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Null(saved!.BrandCompanyName);
    }

    [Fact]
    public async Task HandleAsync_WithATooLongName_IsRejected()
    {
        var fixture = CreateFixture();
        var tooLong = new string('a', UpdateSiteBrandingHandler.MaxLength + 1);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateSiteBranding.UpdateSiteBranding(SiteId, OperatorId, tooLong), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.BrandCompanyNameTooLong", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateSiteBranding.UpdateSiteBranding(SiteId, OperatorId, "Acme"), CancellationToken.None);

        Assert.True(result.IsFailure);
    }
}
