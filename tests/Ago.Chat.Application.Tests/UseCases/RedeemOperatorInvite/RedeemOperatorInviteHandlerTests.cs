using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RedeemOperatorInvite;

/// <summary>
/// The handler's own job is small on purpose - hash the presented code, delegate to
/// <see cref="IOperatorInviteRedemptionRepository"/>, map every outcome back to a `Result`/`Error`
/// (`RedeemOperatorInviteHandler`'s own remarks explain why the interesting work lives in the
/// repository instead). These tests prove exactly that mapping, one outcome at a time, with a fake
/// repository that returns a canned result regardless of what it is asked - the real seat-limit/
/// concurrency behaviour is `Ago.Chat.Concurrency.Tests`' job against real Postgres.
/// </summary>
public class RedeemOperatorInviteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    private static Application.UseCases.RedeemOperatorInvite.RedeemOperatorInviteHandler CreateHandler(
        OperatorInviteRedemptionResult result, out FakeOperatorInviteRedemptionRepository redemptions)
    {
        redemptions = new FakeOperatorInviteRedemptionRepository(result);
        return new Application.UseCases.RedeemOperatorInvite.RedeemOperatorInviteHandler(redemptions, new FakeClock(Now));
    }

    [Fact]
    public async Task HandleAsync_HashesTheCodeBeforeDelegating_NeverSendsThePlaintext()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.Success(OperatorId, SiteId), out var redemptions);

        await handler.HandleAsync(
            new Application.UseCases.RedeemOperatorInvite.RedeemOperatorInvite("sub-123", "invite_abc123"),
            CancellationToken.None);

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes("invite_abc123"));
        Assert.Equal(expectedHash, redemptions.LastAttempt!.CodeHash);
        Assert.Equal("sub-123", redemptions.LastAttempt.ExternalSubjectId);
        Assert.Equal(Now, redemptions.LastAttempt.Now);
    }

    [Fact]
    public async Task HandleAsync_OnSuccess_ReturnsTheOperatorAndSiteIds()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.Success(OperatorId, SiteId), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemOperatorInvite.RedeemOperatorInvite("sub-123", "invite_abc123"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperatorId, result.Value.OperatorId);
        Assert.Equal(SiteId, result.Value.SiteId);
    }

    [Fact]
    public async Task HandleAsync_OnNotFound_ReturnsOperatorInviteNotFound()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.NotFound(), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemOperatorInvite.RedeemOperatorInvite("sub-123", "wrong-code"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_OnExpired_ReturnsOperatorInviteExpired()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.Expired(), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemOperatorInvite.RedeemOperatorInvite("sub-123", "invite_abc123"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.Expired", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_OnAlreadyRedeemed_ReturnsOperatorInviteAlreadyRedeemed()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.AlreadyRedeemed(), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemOperatorInvite.RedeemOperatorInvite("sub-123", "invite_abc123"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.AlreadyRedeemed", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_OnAlreadyOperatorOnSite_ReturnsOperatorInviteAlreadyOperatorOnSite()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.AlreadyOperatorOnSite(), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemOperatorInvite.RedeemOperatorInvite("sub-123", "invite_abc123"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.AlreadyOperatorOnSite", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_OnSeatLimitReached_ReturnsOperatorInviteSeatLimitReachedWithTheLimit()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.SeatLimitReached(2), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemOperatorInvite.RedeemOperatorInvite("sub-123", "invite_abc123"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.SeatLimitReached", result.Error!.Value.Code);
        Assert.Contains("2", result.Error.Value.Message);
    }

    /// <summary>`25-25`: the Administrator-seat counterpart to the seat-limit mapping above - proves
    /// the switch arm exists and names the right limit, the same shape that test already proves for
    /// its sibling.</summary>
    [Fact]
    public async Task HandleAsync_OnAdminLimitReached_ReturnsOperatorInviteAdminLimitReachedWithTheLimit()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.AdminLimitReached(1), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemOperatorInvite.RedeemOperatorInvite("sub-123", "invite_abc123"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.AdminLimitReached", result.Error!.Value.Code);
        Assert.Contains("1", result.Error.Value.Message);
    }

    /// <summary>`25-73`'s own Done-when: "revoking before acceptance is proven to actually block a later
    /// redemption attempt with the stated message" - this is the handler-mapping half of that proof
    /// (the repository's own pre-lock check is proven separately, against real Postgres, by
    /// `Ago.Chat.Concurrency.Tests`/`Ago.Chat.Integration.Tests`). Fails-before: reverting the new
    /// `Revoked` switch arm in `RedeemOperatorInviteHandler` makes this fail with an
    /// `InvalidOperationException` ("Unhandled OperatorInviteRedemptionResult: Revoked") instead of a
    /// clean `Result`.</summary>
    [Fact]
    public async Task HandleAsync_OnRevoked_ReturnsOperatorInviteRevoked()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.Revoked(), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemOperatorInvite.RedeemOperatorInvite("sub-123", "invite_abc123"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.Revoked", result.Error!.Value.Code);
    }

    /// <summary>`25-73`'s own real security boundary, the handler-mapping half: the code alone is not
    /// enough once the repository reports the authenticated caller's email did not match the invite's
    /// own. Fails-before: the identical "unhandled switch arm throws" failure
    /// <see cref="HandleAsync_OnRevoked_ReturnsOperatorInviteRevoked"/>'s own remarks describe, for
    /// `EmailMismatch` instead of `Revoked`.</summary>
    [Fact]
    public async Task HandleAsync_OnEmailMismatch_ReturnsOperatorInviteEmailMismatch()
    {
        var handler = CreateHandler(new OperatorInviteRedemptionResult.EmailMismatch(), out var redemptions);

        var result = await handler.HandleAsync(
            new Application.UseCases.RedeemOperatorInvite.RedeemOperatorInvite(
                "sub-123", "invite_abc123", Email: "someone-else@example.com"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.EmailMismatch", result.Error!.Value.Code);
        // The handler still passes the presented email through to the repository unchanged - the
        // comparison itself is the repository's own job (RedeemOperatorInviteHandler's class-level
        // remarks: "hash, delegate, map every outcome").
        Assert.Equal("someone-else@example.com", redemptions.LastAttempt!.Email);
    }
}
