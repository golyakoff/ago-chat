using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RedeemPendingOperatorInviteForCaller;

/// <summary>
/// `25-85`: the identical "hash, delegate, map every outcome" mapping proof
/// `RedeemOperatorInviteHandlerTests` already gives the code-based sibling, for the no-code path -
/// with no hash to compute here, these tests prove the claims pass through unchanged and every
/// repository outcome maps to the right `Result`/`Error`, using a fake repository that returns a
/// canned result regardless of what it is asked (the same split that file's own remarks describe: the
/// real by-email lookup/ambiguity/transaction behaviour is `OperatorInviteRedemptionRepository`'s own
/// job, proven against real Postgres by the integration suite instead).
/// </summary>
public class RedeemPendingOperatorInviteForCallerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    private static Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCallerHandler CreateHandler(
        OperatorInviteRedemptionResult result, out FakeOperatorInviteRedemptionRepository redemptions)
    {
        redemptions = new FakeOperatorInviteRedemptionRepository(result);
        return new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCallerHandler(
            redemptions, new FakeClock(Now));
    }

    [Fact]
    public async Task HandleAsync_PassesTheCallersOwnClaimsThroughUnchanged_NeverAComputedHash()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.Success(OperatorId, SiteId), out var redemptions);

        await handler.HandleAsync(
            new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCaller(
                "sub-123", "invitee@example.test", "Invitee Name"),
            CancellationToken.None);

        Assert.Equal("invitee@example.test", redemptions.LastPendingAttempt!.Email);
        Assert.Equal("sub-123", redemptions.LastPendingAttempt.ExternalSubjectId);
        Assert.Equal("Invitee Name", redemptions.LastPendingAttempt.Name);
        Assert.Equal(Now, redemptions.LastPendingAttempt.Now);
    }

    [Fact]
    public async Task HandleAsync_OnSuccess_ReturnsTheOperatorAndSiteIds()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.Success(OperatorId, SiteId), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCaller(
                "sub-123", "invitee@example.test"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperatorId, result.Value.OperatorId);
        Assert.Equal(SiteId, result.Value.SiteId);
    }

    /// <summary>Fails-before: before this handler existed at all, there was nothing to fail - once it
    /// exists, reverting its `NotFound` switch arm makes this fail with an `InvalidOperationException`
    /// ("Unhandled OperatorInviteRedemptionResult: NotFound") instead of a clean `Result`, the identical
    /// shape `RedeemOperatorInviteHandlerTests`'s own fails-before entries describe.</summary>
    [Fact]
    public async Task HandleAsync_OnNotFound_ReturnsNoAutoRedeemablePendingInvite()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.NotFound(), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCaller(
                "sub-123", "nobody-invited@example.test"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.NoAutoRedeemablePendingInvite", result.Error!.Value.Code);
    }

    /// <summary>`25-85`'s own new case, distinct from `NotFound` at the repository level but mapped to
    /// the identical client-facing code - `ConversationErrors.OperatorInviteNoAutoRedeemablePendingInvite`'s
    /// own remarks on why both leave the console with the same honest next step (fall back to the
    /// manual code field) rather than two different error messages for what is, from the caller's own
    /// point of view, the same "could not auto-redeem for you" outcome.</summary>
    [Fact]
    public async Task HandleAsync_OnAmbiguous_ReturnsTheIdenticalNoAutoRedeemablePendingInviteCodeAsNotFound()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.Ambiguous(), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCaller(
                "sub-123", "agency-operator@example.test"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.NoAutoRedeemablePendingInvite", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_OnExpired_ReturnsOperatorInviteExpired()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.Expired(), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCaller(
                "sub-123", "invitee@example.test"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.Expired", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_OnAlreadyRedeemed_ReturnsOperatorInviteAlreadyRedeemed()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.AlreadyRedeemed(), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCaller(
                "sub-123", "invitee@example.test"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.AlreadyRedeemed", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_OnAlreadyOperatorOnSite_ReturnsOperatorInviteAlreadyOperatorOnSite()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.AlreadyOperatorOnSite(), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCaller(
                "sub-123", "invitee@example.test"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.AlreadyOperatorOnSite", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_OnSeatLimitReached_ReturnsOperatorInviteSeatLimitReachedWithTheLimit()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.SeatLimitReached(2), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCaller(
                "sub-123", "invitee@example.test"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.SeatLimitReached", result.Error!.Value.Code);
        Assert.Contains("2", result.Error.Value.Message);
    }

    [Fact]
    public async Task HandleAsync_OnAdminLimitReached_ReturnsOperatorInviteAdminLimitReachedWithTheLimit()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.AdminLimitReached(1), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCaller(
                "sub-123", "invitee@example.test"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.AdminLimitReached", result.Error!.Value.Code);
        Assert.Contains("1", result.Error.Value.Message);
    }

    [Fact]
    public async Task HandleAsync_OnRevoked_ReturnsOperatorInviteRevoked()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.Revoked(), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCaller(
                "sub-123", "invitee@example.test"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.Revoked", result.Error!.Value.Code);
    }

    /// <summary>`EmailMismatch` is unreachable through this handler by construction (the repository
    /// looks the invite up *by* the caller's own email, so it can never disagree) - this proves the
    /// defensive default arm actually throws rather than silently mapping to a `Result`, matching
    /// `RedeemOperatorInviteHandler`'s own "no legal recourse for a caller" throw shape for an
    /// equally-unreachable case.</summary>
    [Fact]
    public async Task HandleAsync_OnEmailMismatch_ThrowsAsUnreachable()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.EmailMismatch(), out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new Application.UseCases.RedeemPendingOperatorInviteForCaller.RedeemPendingOperatorInviteForCaller(
                "sub-123", "invitee@example.test"),
            CancellationToken.None));
    }
}
