using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetAssignmentPenalty;
using Ago.Chat.Application.UseCases.UpdateAssignmentPenalty;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.UpdateAssignmentPenalty;

/// <summary>`23-05`: the console's own read/write pair - the `site:configure` gate every sibling
/// settings screen already uses, and the `SiteSettingsChanged` outbox row every sibling write already
/// produces (`SiteAssignmentPenaltyUpdated`'s own remarks explain why, even though the one real reader
/// that matters, the assignment claimers, never goes through the cache that event evicts).</summary>
public class UpdateAssignmentPenaltyHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        UpdateAssignmentPenaltyHandler Handler,
        GetAssignmentPenaltyHandler Reader,
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
            new UpdateAssignmentPenaltyHandler(sites, permissions, outbox, new FakeIdGenerator(), new FakeClock(Now)),
            new GetAssignmentPenaltyHandler(sites, permissions),
            sites,
            outbox);
    }

    [Fact]
    public async Task ANewSite_DefaultsToTwoMinutes()
    {
        var fixture = CreateFixture();

        var result = await fixture.Reader.HandleAsync(new GetAssignmentPenalty(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(120, result.Value);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_StoresTheValue()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateAssignmentPenalty.UpdateAssignmentPenalty(SiteId, OperatorId, 300),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(300, result.Value);
        var saved = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(300, saved!.AssignmentPenaltySeconds);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_EnqueuesExactlyOneSiteSettingsChangedEnvelope()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateAssignmentPenalty.UpdateAssignmentPenalty(SiteId, OperatorId, 300),
            CancellationToken.None);

        var envelope = Assert.Single(fixture.Outbox.Enqueued);
        Assert.Equal(nameof(SiteSettingsChanged), envelope.Type);
    }

    [Fact]
    public async Task HandleAsync_WithoutSiteConfigure_IsForbidden_AndWritesNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateAssignmentPenalty.UpdateAssignmentPenalty(SiteId, OperatorId, 300),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
        var site = await fixture.Sites.GetByIdAsync(SiteId, CancellationToken.None);
        Assert.Equal(120, site!.AssignmentPenaltySeconds);
    }

    [Fact]
    public async Task GetAsync_WithoutSiteConfigure_IsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Reader.HandleAsync(new GetAssignmentPenalty(SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task HandleAsync_WithANonPositiveValue_IsARejectionRatherThanAThrow(int seconds)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.UpdateAssignmentPenalty.UpdateAssignmentPenalty(SiteId, OperatorId, seconds),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("AssignmentPenalty.Invalid", result.Error!.Value.Code);
        Assert.Empty(fixture.Outbox.Enqueued);
    }
}
