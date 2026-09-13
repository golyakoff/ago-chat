using Ago.Chat.Application.Tests.Fakes;

namespace Ago.Chat.Application.Tests.UseCases.HasPendingOperatorInvite;

public class HasPendingOperatorInviteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WhenTheEmailHasAPendingInvite_ReturnsTrue()
    {
        var invites = new FakePendingOperatorInviteByEmailReadStore();
        invites.SeedPending("invitee@example.com");
        var handler = new Application.UseCases.HasPendingOperatorInvite.HasPendingOperatorInviteHandler(invites, new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.HasPendingOperatorInvite.HasPendingOperatorInvite("invitee@example.com"), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task HandleAsync_WhenTheEmailHasNoPendingInvite_ReturnsFalse()
    {
        var invites = new FakePendingOperatorInviteByEmailReadStore();
        var handler = new Application.UseCases.HasPendingOperatorInvite.HasPendingOperatorInviteHandler(invites, new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.HasPendingOperatorInvite.HasPendingOperatorInvite("nobody@example.com"), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task HandleAsync_PassesTheExactEmailThrough()
    {
        var invites = new FakePendingOperatorInviteByEmailReadStore();
        var handler = new Application.UseCases.HasPendingOperatorInvite.HasPendingOperatorInviteHandler(invites, new FakeClock(Now));

        await handler.HandleAsync(
            new Application.UseCases.HasPendingOperatorInvite.HasPendingOperatorInvite("someone@example.com"), CancellationToken.None);

        Assert.Equal("someone@example.com", invites.LastEmailChecked);
    }
}
