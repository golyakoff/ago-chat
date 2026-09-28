using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ListOperatorInvites;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListOperatorInvites;

/// <summary>`26-263`: the effective team-membership status the «Команда → Люди» page reads is computed
/// here, in the handler, against <c>IClock</c> - never in the read store's SQL (`adr/0011`). These tests
/// pin each of the five cases and the redeemed-invite split into <c>InTeam</c> versus <c>Removed</c>, the
/// distinction the delivery <see cref="OperatorInviteListStatus"/> cannot express.</summary>
public class ListOperatorInvitesHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId RequestedBy = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(ListOperatorInvitesHandler Handler, FakeOperatorInviteListReadStore Invites);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var invites = new FakeOperatorInviteListReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(RequestedBy, SiteId, Permission.SiteManageOperators);
        }

        var handler = new ListOperatorInvitesHandler(invites, permissions, new FakeClock(Now));
        return new Fixture(handler, invites);
    }

    // Every raw fact the read store returns, defaulted to a still-pending invite - each test overrides only
    // the facts its own case turns on, so the case under test is the only thing that varies.
    private static OperatorInviteListItem Row(
        DateTimeOffset? redeemedAt = null,
        DateTimeOffset? revokedAt = null,
        DateTimeOffset? expiresAt = null,
        Guid? redeemedByOperatorId = null,
        DateTimeOffset? redeemedOperatorRemovedAt = null,
        string? sendFailureCode = null) =>
        new(
            new OperatorInviteId(Guid.NewGuid()),
            "invitee@example.invalid",
            CreatedAt: Now.AddDays(-1),
            ExpiresAt: expiresAt ?? Now.AddDays(1),
            RedeemedAt: redeemedAt,
            RevokedAt: revokedAt,
            SendFailureCode: sendFailureCode,
            RoleNames: ["Operator"],
            RedeemedByOperatorId: redeemedByOperatorId,
            RedeemedOperatorRemovedAt: redeemedOperatorRemovedAt);

    [Fact]
    public async Task HandleAsync_WhenCallerLacksPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(new Application.UseCases.ListOperatorInvites.ListOperatorInvites(RequestedBy, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_APendingInvite_IsEffectiveStatusPending()
    {
        var fixture = CreateFixture();
        fixture.Invites.Seed(SiteId, Row());

        var entry = await SingleEntryAsync(fixture);

        Assert.Equal(OperatorInviteEffectiveStatus.Pending, entry.EffectiveStatus);
        Assert.Null(entry.RedeemedAt);
        Assert.Null(entry.RemovedAt);
    }

    [Fact]
    public async Task HandleAsync_ARedeemedInviteWhoseOperatorIsStillActive_IsInTeamWithRedeemedAt()
    {
        var fixture = CreateFixture();
        var redeemedAt = Now.AddHours(-3);
        fixture.Invites.Seed(SiteId, Row(redeemedAt: redeemedAt, redeemedByOperatorId: Guid.NewGuid()));

        var entry = await SingleEntryAsync(fixture);

        Assert.Equal(OperatorInviteEffectiveStatus.InTeam, entry.EffectiveStatus);
        Assert.Equal(redeemedAt, entry.RedeemedAt);
        Assert.Null(entry.RemovedAt);
    }

    /// <summary>The correlation this slice exists to make: an invite that WAS redeemed, whose operator has
    /// since been soft-removed, must read as <c>Removed</c> and carry that operator's removal instant -
    /// not as a bare <c>InTeam</c> the way it would if the join to `operators.removed_at` were dropped.
    /// Fails-before: an implementation that ignored <c>RedeemedOperatorRemovedAt</c> and mapped every
    /// redeemed invite to <c>InTeam</c> would return <c>InTeam</c> with a null <c>RemovedAt</c> here.</summary>
    [Fact]
    public async Task HandleAsync_ARedeemedInviteWhoseOperatorWasRemoved_IsRemovedWithRemovedAt()
    {
        var fixture = CreateFixture();
        var redeemedAt = Now.AddDays(-5);
        var removedAt = Now.AddHours(-2);
        fixture.Invites.Seed(SiteId, Row(
            redeemedAt: redeemedAt, redeemedByOperatorId: Guid.NewGuid(), redeemedOperatorRemovedAt: removedAt));

        var entry = await SingleEntryAsync(fixture);

        Assert.Equal(OperatorInviteEffectiveStatus.Removed, entry.EffectiveStatus);
        Assert.Equal(redeemedAt, entry.RedeemedAt);
        Assert.Equal(removedAt, entry.RemovedAt);
    }

    [Fact]
    public async Task HandleAsync_ARevokedInvite_IsEffectiveStatusRevoked()
    {
        var fixture = CreateFixture();
        fixture.Invites.Seed(SiteId, Row(revokedAt: Now.AddHours(-1)));

        var entry = await SingleEntryAsync(fixture);

        Assert.Equal(OperatorInviteEffectiveStatus.Revoked, entry.EffectiveStatus);
        Assert.Null(entry.RemovedAt);
    }

    [Fact]
    public async Task HandleAsync_AnUnredeemedInvitePastItsExpiry_IsEffectiveStatusExpired()
    {
        var fixture = CreateFixture();
        fixture.Invites.Seed(SiteId, Row(expiresAt: Now.AddHours(-1)));

        var entry = await SingleEntryAsync(fixture);

        Assert.Equal(OperatorInviteEffectiveStatus.Expired, entry.EffectiveStatus);
        Assert.Null(entry.RedeemedAt);
    }

    /// <summary>Revoked is terminal and outranks a clock-based Expired even when both facts are true at
    /// once - the same priority `OperatorInviteEffectiveStatus`'s own remarks pin.</summary>
    [Fact]
    public async Task HandleAsync_ARevokedInviteAlsoPastExpiry_IsRevokedNotExpired()
    {
        var fixture = CreateFixture();
        fixture.Invites.Seed(SiteId, Row(revokedAt: Now.AddHours(-1), expiresAt: Now.AddHours(-2)));

        var entry = await SingleEntryAsync(fixture);

        Assert.Equal(OperatorInviteEffectiveStatus.Revoked, entry.EffectiveStatus);
    }

    private static async Task<OperatorInviteListEntry> SingleEntryAsync(Fixture fixture)
    {
        var result = await fixture.Handler.HandleAsync(new Application.UseCases.ListOperatorInvites.ListOperatorInvites(RequestedBy, SiteId), CancellationToken.None);
        Assert.True(result.IsSuccess);
        return Assert.Single(result.Value);
    }
}
