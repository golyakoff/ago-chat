using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RevokeChannelCredential;

public class RevokeChannelCredentialHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.RevokeChannelCredential.RevokeChannelCredentialHandler Handler,
        FakeChannelCredentialRepository Credentials, ChannelCredential Credential);

    private static Fixture CreateFixture(bool grantPermission = true, SiteId? credentialSiteId = null)
    {
        var credentials = new FakeChannelCredentialRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ChannelManage);
        }

        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), credentialSiteId ?? SiteId, ChannelKind.Max,
            [1, 2, 3], [4, 5, 6], Now);
        credentials.Seed(credential);

        return new Fixture(
            new Application.UseCases.RevokeChannelCredential.RevokeChannelCredentialHandler(credentials, permissions),
            credentials, credential);
    }

    [Fact]
    public async Task HandleAsync_FlipsActiveToFalse()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeChannelCredential.RevokeChannelCredential(fixture.Credential.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var stored = await fixture.Credentials.GetByIdAsync(fixture.Credential.Id, CancellationToken.None);
        Assert.False(stored!.Active);
    }

    [Fact]
    public async Task HandleAsync_CalledTwice_IsIdempotent()
    {
        var fixture = CreateFixture();
        var command = new Application.UseCases.RevokeChannelCredential.RevokeChannelCredential(fixture.Credential.Id, OperatorId, SiteId);

        var first = await fixture.Handler.HandleAsync(command, CancellationToken.None);
        var second = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksChannelManage_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeChannelCredential.RevokeChannelCredential(fixture.Credential.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheCredentialBelongsToAnotherSite_ReturnsNotFound()
    {
        var otherSite = new SiteId(Guid.NewGuid());
        var fixture = CreateFixture(credentialSiteId: otherSite);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeChannelCredential.RevokeChannelCredential(fixture.Credential.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelCredential.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheCredentialDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RevokeChannelCredential.RevokeChannelCredential(
                new ChannelCredentialId(Guid.NewGuid()), OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelCredential.NotFound", result.Error!.Value.Code);
    }
}
