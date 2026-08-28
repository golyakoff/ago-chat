using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateOperatorInvite;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.CreateOperatorInvite;

public class CreateOperatorInviteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OperatorRoleId = Guid.NewGuid();

    private sealed record Fixture(
        Application.UseCases.CreateOperatorInvite.CreateOperatorInviteHandler Handler,
        FakeOperatorInviteRepository Invites,
        FakePermissionChecker Permissions,
        FakeRoleRepository Roles);

    private static Fixture CreateFixture(bool grantPermission = true, TimeSpan? validFor = null)
    {
        var invites = new FakeOperatorInviteRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteManageOperators);
        }

        var roles = new FakeRoleRepository();
        roles.Seed(SiteId, "Operator", OperatorRoleId);

        var handler = new Application.UseCases.CreateOperatorInvite.CreateOperatorInviteHandler(
            invites, roles, permissions, new FakeOperatorInviteCodeGenerator("invite_abc123"),
            new OperatorInviteOptions { ValidFor = validFor ?? TimeSpan.FromDays(7) },
            new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, invites, permissions, roles);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsTheGeneratedCode()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateOperatorInvite.CreateOperatorInvite(OperatorId, SiteId, "Operator"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("invite_abc123", result.Value.Code);
        Assert.Equal(Now + TimeSpan.FromDays(7), result.Value.ExpiresAt);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_PersistsTheInviteWithTheResolvedRoleId()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateOperatorInvite.CreateOperatorInvite(OperatorId, SiteId, "Operator"),
            CancellationToken.None);

        var saved = fixture.Invites.Get(new OperatorInviteId(result.Value.OperatorInviteId));
        Assert.NotNull(saved);
        Assert.Equal(OperatorRoleId, saved.RoleId);
        Assert.Equal(SiteId, saved.SiteId);
        Assert.Equal(OperatorId, saved.CreatedByOperatorId);
        Assert.False(saved.IsRedeemed);
    }

    [Fact]
    public async Task HandleAsync_NeverPersistsThePlaintextCode()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateOperatorInvite.CreateOperatorInvite(OperatorId, SiteId, "Operator"),
            CancellationToken.None);

        var saved = fixture.Invites.Get(new OperatorInviteId(result.Value.OperatorInviteId));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes("invite_abc123"));
        Assert.Equal(expectedHash, saved!.CodeHash);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteManageOperators_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateOperatorInvite.CreateOperatorInvite(OperatorId, SiteId, "Operator"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheRoleNameDoesNotExistOnThisSite_ReturnsInvalidRole()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CreateOperatorInvite.CreateOperatorInvite(OperatorId, SiteId, "SuperAdmin"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.InvalidRole", result.Error!.Value.Code);
    }
}
