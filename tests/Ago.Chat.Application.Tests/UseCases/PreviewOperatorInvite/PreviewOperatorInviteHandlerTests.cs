using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.PreviewOperatorInvite;

namespace Ago.Chat.Application.Tests.UseCases.PreviewOperatorInvite;

/// <summary>
/// `23-70`: proves two things a fake read store cannot prove by itself - that the handler hashes the
/// presented code before ever asking the read store about it (the same "never sends the plaintext"
/// discipline `RedeemOperatorInviteHandlerTests` already proves for the redemption side of this exact
/// code), and that the three-way status is decided here, against a fake clock, rather than trusted from
/// the row. No permission check anywhere in these tests - deliberately: this handler never calls
/// <c>IPermissionChecker</c> at all (`PreviewOperatorInviteHandler`'s own remarks on why).
/// </summary>
public class PreviewOperatorInviteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static PreviewOperatorInviteHandler CreateHandler(
        OperatorInvitePreviewItem? item, out FakeOperatorInvitePreviewReadStore previews)
    {
        previews = new FakeOperatorInvitePreviewReadStore(item);
        return new PreviewOperatorInviteHandler(previews, new FakeClock(Now));
    }

    [Fact]
    public async Task HandleAsync_HashesTheCodeBeforeAskingTheReadStore_NeverSendsThePlaintext()
    {
        var handler = CreateHandler(
            new OperatorInvitePreviewItem("Acme", "Jane", Now.AddDays(1), IsRedeemed: false), out var previews);

        await handler.HandleAsync(new Application.UseCases.PreviewOperatorInvite.PreviewOperatorInvite("invite_abc123"), CancellationToken.None);

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes("invite_abc123"));
        Assert.Equal(expectedHash, previews.LastCodeHash);
    }

    [Fact]
    public async Task HandleAsync_OnNoMatchingRow_ReturnsOperatorInviteNotFound()
    {
        var handler = CreateHandler(null, out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.PreviewOperatorInvite.PreviewOperatorInvite("wrong-code"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenNotRedeemedAndNotYetExpired_ReturnsValid()
    {
        var handler = CreateHandler(
            new OperatorInvitePreviewItem("Acme", "Jane", Now.AddMinutes(1), IsRedeemed: false), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.PreviewOperatorInvite.PreviewOperatorInvite("invite_abc123"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperatorInvitePreviewStatus.Valid, result.Value.Status);
        Assert.Equal("Acme", result.Value.SiteName);
        Assert.Equal("Jane", result.Value.InvitedByDisplayName);
    }

    /// <summary>The exact boundary <c>OperatorInvite.IsExpired</c> itself uses (`now &gt;= ExpiresAt`) -
    /// this handler's own remarks state it mirrors that domain method's comparison rather than
    /// inventing a second one.</summary>
    [Fact]
    public async Task HandleAsync_WhenExpiresAtEqualsNow_ReturnsExpired()
    {
        var handler = CreateHandler(new OperatorInvitePreviewItem("Acme", "Jane", Now, IsRedeemed: false), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.PreviewOperatorInvite.PreviewOperatorInvite("invite_abc123"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperatorInvitePreviewStatus.Expired, result.Value.Status);
    }

    [Fact]
    public async Task HandleAsync_WhenExpiryIsInThePast_ReturnsExpired()
    {
        var handler = CreateHandler(
            new OperatorInvitePreviewItem("Acme", "Jane", Now.AddDays(-1), IsRedeemed: false), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.PreviewOperatorInvite.PreviewOperatorInvite("invite_abc123"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperatorInvitePreviewStatus.Expired, result.Value.Status);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyRedeemed_ReturnsRedeemed_EvenIfStillBeforeExpiry()
    {
        var handler = CreateHandler(
            new OperatorInvitePreviewItem("Acme", "Jane", Now.AddDays(1), IsRedeemed: true), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.PreviewOperatorInvite.PreviewOperatorInvite("invite_abc123"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(OperatorInvitePreviewStatus.Redeemed, result.Value.Status);
    }

    [Fact]
    public async Task HandleAsync_WhenInvitedByIsNull_StillReturnsSiteNameAndStatus()
    {
        var handler = CreateHandler(
            new OperatorInvitePreviewItem("Acme", null, Now.AddDays(1), IsRedeemed: false), out _);

        var result = await handler.HandleAsync(
            new Application.UseCases.PreviewOperatorInvite.PreviewOperatorInvite("invite_abc123"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.InvitedByDisplayName);
        Assert.Equal(OperatorInvitePreviewStatus.Valid, result.Value.Status);
    }
}
