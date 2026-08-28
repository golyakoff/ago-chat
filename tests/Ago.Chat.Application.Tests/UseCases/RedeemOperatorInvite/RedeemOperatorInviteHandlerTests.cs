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
}
