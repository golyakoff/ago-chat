using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.AddRolePermissionsAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.AddRolePermissionsAsOwner;

/// <summary>`25-76`: the platform owner's own role-permission tool - every port faked, the identical
/// shape <c>SetUnconditionalModuleGrantAsOwnerHandlerTests</c> already establishes for its sibling.
/// What a real Postgres transaction and outbox publish do is
/// <c>Ago.Chat.Integration.Tests.AddRolePermissionsAsOwnerHandlerTests</c>' job.</summary>
public sealed class AddRolePermissionsAsOwnerHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    private static Application.UseCases.AddRolePermissionsAsOwner.AddRolePermissionsAsOwner Command(
        string roleName = "Admin", params string[] permissions) =>
        new(SiteId, roleName, permissions);

    private static (Application.UseCases.AddRolePermissionsAsOwner.AddRolePermissionsAsOwnerHandler Handler, FakeRoleRepository Roles) CreateFixture()
    {
        var roles = new FakeRoleRepository();
        return (new Application.UseCases.AddRolePermissionsAsOwner.AddRolePermissionsAsOwnerHandler(roles), roles);
    }

    /// <summary>Fails-before: a permission string that is not a real, known <see cref="Permission"/> is
    /// refused, not silently added - the closed-vocabulary check this item's own brief requires, proven
    /// against a role that does exist so the refusal cannot be mistaken for "no such role".</summary>
    [Fact]
    public async Task HandleAsync_WithAnUnknownPermission_Refuses_AndAddsNothing()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), [Permission.SiteConfigure.Value]);

        var result = await handler.HandleAsync(Command("Admin", "conversation:teleport"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.PermissionUnknown", result.Error!.Value.Code);
        Assert.Equal([Permission.SiteConfigure.Value], roles.PermissionsFor(SiteId, "Admin"));
    }

    /// <summary>A mixed request - one real permission alongside one typo'd one - is refused as a whole,
    /// not partially applied (<see cref="Application.UseCases.AddRolePermissionsAsOwner.AddRolePermissionsAsOwner"/>'s
    /// own remarks on why a partial grant is not obviously a kindness).</summary>
    [Fact]
    public async Task HandleAsync_WithOneRealAndOneUnknownPermission_RefusesTheWholeRequest()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), []);

        var result = await handler.HandleAsync(
            Command("Admin", Permission.ChannelManage.Value, "not:a-real-permission"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.PermissionUnknown", result.Error!.Value.Code);
        Assert.Empty(roles.PermissionsFor(SiteId, "Admin"));
    }

    [Fact]
    public async Task HandleAsync_WithNoPermissions_Refuses()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), []);

        var result = await handler.HandleAsync(Command("Admin"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.PermissionsRequired", result.Error!.Value.Code);
    }

    /// <summary>The named role does not exist on this site - reuses `Operator.RoleNotFound`, the
    /// identical code `ChangeOperatorRoleHandler` already gives the same miss on a different write
    /// path (<see cref="Application.UseCases.ConversationErrors.OperatorRoleNotFound"/>'s own
    /// remarks).</summary>
    [Fact]
    public async Task HandleAsync_WithARoleNameThatDoesNotExistOnThisSite_Refuses()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(Command("SuperAdmin", Permission.ChannelManage.Value), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.RoleNotFound", result.Error!.Value.Code);
    }

    /// <summary>The headline case this item exists for: the real gap found live twice in one evening -
    /// a tenant's `Admin` role missing `channel:manage` - closed through this handler.</summary>
    [Fact]
    public async Task HandleAsync_WithARealMissingPermission_AddsIt()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), [Permission.SiteConfigure.Value]);

        var result = await handler.HandleAsync(Command("Admin", Permission.ChannelManage.Value), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[] { Permission.SiteConfigure.Value, Permission.ChannelManage.Value }.OrderBy(p => p, StringComparer.Ordinal),
            roles.PermissionsFor(SiteId, "Admin").OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>More than one permission in a single call, the same shape a role missing several
    /// permissions at once needs (this item's own two real gaps: `conversation:close` and
    /// `channel:manage`, on different tenants, could be two calls or - here - one.</summary>
    [Fact]
    public async Task HandleAsync_WithMultiplePermissions_AddsAllOfThem()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Operator", Guid.NewGuid(), []);

        var result = await handler.HandleAsync(
            Command("Operator", Permission.ConversationClose.Value, Permission.ChannelManage.Value), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[] { Permission.ConversationClose.Value, Permission.ChannelManage.Value }.OrderBy(p => p, StringComparer.Ordinal),
            roles.PermissionsFor(SiteId, "Operator").OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>Re-granting an already-held permission is a no-op, never an error - the identical
    /// idempotence <see cref="Application.Abstractions.IRoleRepository.AddPermissionsAsync"/>'s own
    /// remarks promise, exercised through this handler rather than assumed.</summary>
    [Fact]
    public async Task HandleAsync_WithAPermissionTheRoleAlreadyHolds_Succeeds_AsANoOp()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), [Permission.ChannelManage.Value]);

        var result = await handler.HandleAsync(Command("Admin", Permission.ChannelManage.Value), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([Permission.ChannelManage.Value], roles.PermissionsFor(SiteId, "Admin"));
    }
}
