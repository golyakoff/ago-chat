using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetContactVisibility;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UpdateContactVisibility;

/// <summary>
/// `23-11`/`decisions.md` §5: the console's read/write pair for the account's contact-visibility
/// rung. `HandleAsync_WhenCalled_EnqueuesBothTheChatInternalAndTheCrossBoundaryEnvelope` is this
/// item's own Done-when, restated as a test: "the event is published from the outbox in the write's
/// own transaction" - both of them, from the one write.
/// </summary>
public class UpdateContactVisibilityHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.UpdateContactVisibility.UpdateContactVisibilityHandler Handler,
        GetContactVisibilityHandler Reader,
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
            new Application.UseCases.UpdateContactVisibility.UpdateContactVisibilityHandler(
                sites, permissions, outbox, new FakeIdGenerator(), new FakeClock(Now)),
            new GetContactVisibilityHandler(sites, permissions),
            sites,
            outbox);
    }

    [Fact]
    public async Task ANewSite_DefaultsToVisible()
    {
        var fixture = CreateFixture();

        var result = await fixture.Reader.HandleAsync(new GetContactVisibility(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ContactVisibility.Visible, result.Value);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_StoresTheValue()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateContactVisibility.UpdateContactVisibility(SiteId, OperatorId, "MaskedWithReveal"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(ContactVisibility.MaskedWithReveal, result.Value);
        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(ContactVisibility.MaskedWithReveal, saved!.ContactVisibility);
    }

    /// <summary>`23-11`'s own Done-when: "the event is published from the outbox in the write's own
    /// transaction" - one domain write, two envelopes, the chat-internal cache-invalidation contract
    /// and the cross-boundary one `23-12`'s calendar-side consumer reads.</summary>
    [Fact]
    public async Task HandleAsync_WhenPermitted_EnqueuesBothTheChatInternalAndTheCrossBoundaryEnvelope()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateContactVisibility.UpdateContactVisibility(SiteId, OperatorId, "MaskedWithReveal"),
            CancellationToken.None);

        Assert.Equal(2, fixture.Outbox.Enqueued.Count);
        Assert.Contains(fixture.Outbox.Enqueued, e => e.Type == nameof(SiteSettingsChanged));
        var crossBoundary = Assert.Single(fixture.Outbox.Enqueued, e => e.Type == nameof(ContactVisibilityChanged));
        var contract = System.Text.Json.JsonSerializer.Deserialize<ContactVisibilityChanged>(crossBoundary.Payload)!;
        Assert.Equal(SiteId.Value, contract.SiteId);
        Assert.Equal("MaskedWithReveal", contract.Rung);
    }

    /// <summary>§5/`ContactVisibility`'s own remarks: rung three is not a value this enum can ever
    /// hold, so a request naming it is refused exactly like any other unrecognised string - this is
    /// the item's own sharpest instruction, proven directly.</summary>
    [Fact]
    public async Task HandleAsync_NamingRungThree_IsRejected_AndWritesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateContactVisibility.UpdateContactVisibility(SiteId, OperatorId, "Never"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ContactVisibility.InvalidRung", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
        var site = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(ContactVisibility.Visible, site!.ContactVisibility);
    }

    [Theory]
    [InlineData("")]
    [InlineData("visible-ish")]
    [InlineData("never")]
    public async Task HandleAsync_WithAnUnrecognisedRung_IsARejectionRatherThanAThrow(string rung)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateContactVisibility.UpdateContactVisibility(SiteId, OperatorId, rung),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ContactVisibility.InvalidRung", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigure_IsForbidden_AndWritesNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateContactVisibility.UpdateContactVisibility(SiteId, OperatorId, "MaskedWithReveal"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
        var site = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(ContactVisibility.Visible, site!.ContactVisibility);
    }

    /// <summary>Tenant isolation: an operator who holds `site:configure` on a *different* site cannot
    /// read or write this one's rung - `IPermissionChecker.HasPermissionAsync` checks the
    /// `(OperatorId, SiteId)` pair, not the permission alone, so a grant on another tenant proves
    /// nothing here.</summary>
    [Fact]
    public async Task HandleAsync_OperatorHoldsSiteConfigureOnADifferentSite_IsForbidden_AndWritesNothing()
    {
        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, "shop_7f3a", []));
        var otherSiteId = new SiteId(Guid.NewGuid());
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, otherSiteId, Permission.SiteConfigure);
        var outbox = new FakeOutboxWriter();
        var handler = new Application.UseCases.UpdateContactVisibility.UpdateContactVisibilityHandler(
            sites, permissions, outbox, new FakeIdGenerator(), new FakeClock(Now));
        var reader = new GetContactVisibilityHandler(sites, permissions);

        var writeResult = await handler.HandleAsync(
            new Application.UseCases.UpdateContactVisibility.UpdateContactVisibility(SiteId, OperatorId, "MaskedWithReveal"),
            CancellationToken.None);
        var readResult = await reader.HandleAsync(new GetContactVisibility(SiteId, OperatorId), CancellationToken.None);

        Assert.True(writeResult.IsFailure);
        Assert.Equal("Conversation.Forbidden", writeResult.Error!.Value.Code);
        Assert.True(readResult.IsFailure);
        Assert.Equal("Conversation.Forbidden", readResult.Error!.Value.Code);
        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task GetAsync_WithoutSiteConfigure_IsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Reader.HandleAsync(new GetContactVisibility(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
