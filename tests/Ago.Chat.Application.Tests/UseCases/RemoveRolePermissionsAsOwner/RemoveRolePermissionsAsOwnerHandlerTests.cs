using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RemoveRolePermissionsAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RemoveRolePermissionsAsOwner;

/// <summary>`25-77`: the platform owner's own removal - every port faked, the identical shape
/// <c>AddRolePermissionsAsOwnerHandlerTests</c> already establishes for its own sibling. What a real
/// Postgres transaction, outbox publish and `role_permission_removal_overrides` row do is
/// <c>Ago.Chat.Integration.Tests</c>' job.</summary>
public sealed class RemoveRolePermissionsAsOwnerHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    private static Application.UseCases.RemoveRolePermissionsAsOwner.RemoveRolePermissionsAsOwner Command(
        string roleName = "Admin", string removedBy = "owner-sub-123", string reason = "cleaning up a stale grant",
        params string[] permissions) =>
        new(SiteId, roleName, permissions, removedBy, reason);

    private static (Application.UseCases.RemoveRolePermissionsAsOwner.RemoveRolePermissionsAsOwnerHandler Handler, FakeRoleRepository Roles) CreateFixture()
    {
        var roles = new FakeRoleRepository();
        return (new Application.UseCases.RemoveRolePermissionsAsOwner.RemoveRolePermissionsAsOwnerHandler(roles), roles);
    }

    [Fact]
    public async Task HandleAsync_WithNoReasonAtAll_Refuses_AndRemovesNothing()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), [Permission.SiteConfigure.Value]);

        var result = await handler.HandleAsync(Command(reason: "", permissions: Permission.SiteConfigure.Value), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.PermissionRemovalReasonRequired", result.Error!.Value.Code);
        Assert.Equal([Permission.SiteConfigure.Value], roles.PermissionsFor(SiteId, "Admin"));
        Assert.Empty(roles.RemovalOverrides);
    }

    [Fact]
    public async Task HandleAsync_WithAWhitespaceOnlyReason_Refuses()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), [Permission.SiteConfigure.Value]);

        var result = await handler.HandleAsync(Command(reason: "   ", permissions: Permission.SiteConfigure.Value), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.PermissionRemovalReasonRequired", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithAReasonOverTheBound_Refuses()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), [Permission.SiteConfigure.Value]);

        var result = await handler.HandleAsync(
            Command(
                reason: new string('a', Application.UseCases.RemoveRolePermissionsAsOwner.RemoveRolePermissionsAsOwnerHandler.MaxReasonLength + 1),
                permissions: Permission.SiteConfigure.Value),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.PermissionRemovalReasonRequired", result.Error!.Value.Code);
    }

    /// <summary>The reason check runs before the permission list is even inspected - a blank reason on
    /// an otherwise-empty permission list is still refused as a reason problem, not a permissions
    /// problem.</summary>
    [Fact]
    public async Task HandleAsync_WithNoReason_AndNoPermissions_RefusesForTheReason_NotThePermissions()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(Command(reason: ""), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.PermissionRemovalReasonRequired", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithNoPermissions_Refuses()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), [Permission.SiteConfigure.Value]);

        var result = await handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.PermissionsRequired", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithAnUnknownPermission_Refuses_AndRemovesNothing()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), [Permission.SiteConfigure.Value]);

        var result = await handler.HandleAsync(Command(permissions: "conversation:teleport"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Role.PermissionUnknown", result.Error!.Value.Code);
        Assert.Equal([Permission.SiteConfigure.Value], roles.PermissionsFor(SiteId, "Admin"));
        Assert.Empty(roles.RemovalOverrides);
    }

    [Fact]
    public async Task HandleAsync_WithARoleNameThatDoesNotExistOnThisSite_Refuses()
    {
        var (handler, _) = CreateFixture();

        var result = await handler.HandleAsync(Command("SuperAdmin", permissions: Permission.SiteConfigure.Value), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Operator.RoleNotFound", result.Error!.Value.Code);
    }

    /// <summary>"No magic roles" (this item's own headline answer): removing one of `Admin`'s own
    /// defining permissions is not refused, not even with a warning - the owner is trusted.</summary>
    [Fact]
    public async Task HandleAsync_RemovingAnAdminDefiningPermission_Succeeds_NoCarveOut()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), [Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value]);

        var result = await handler.HandleAsync(Command(permissions: Permission.SiteManageOperators.Value), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([Permission.SiteConfigure.Value], roles.PermissionsFor(SiteId, "Admin"));
    }

    /// <summary>The other half of "no magic roles": a role can be reduced to holding nothing at all.
    /// </summary>
    [Fact]
    public async Task HandleAsync_RemovingEveryPermissionARoleHas_LeavesItEmpty_NotRefused()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Operator", Guid.NewGuid(), [Permission.ConversationRead.Value]);

        var result = await handler.HandleAsync(Command("Operator", permissions: Permission.ConversationRead.Value), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(roles.PermissionsFor(SiteId, "Operator"));
    }

    /// <summary>Removing a permission the role never held is a no-op, never an error - the identical
    /// idempotence <see cref="Application.Abstractions.IRoleRepository.RemovePermissionsAsync"/>'s own
    /// remarks promise for the reverse direction of <c>AddPermissionsAsync</c>'s own contract.</summary>
    [Fact]
    public async Task HandleAsync_RemovingAPermissionTheRoleNeverHeld_Succeeds_AsANoOp()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), [Permission.SiteConfigure.Value]);

        var result = await handler.HandleAsync(Command(permissions: Permission.ChannelManage.Value), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([Permission.SiteConfigure.Value], roles.PermissionsFor(SiteId, "Admin"));
    }

    [Fact]
    public async Task HandleAsync_WithMultiplePermissions_RemovesAllOfThem()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(
            SiteId, "Admin", Guid.NewGuid(),
            [Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value]);

        var result = await handler.HandleAsync(
            Command(permissions: [Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value]), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal([Permission.SiteConfigure.Value], roles.PermissionsFor(SiteId, "Admin"));
    }

    /// <summary>The reason actually reaches the port, trimmed - proven against the fake's own recorded
    /// override, the same proof <c>AddRolePermissionsAsOwnerHandlerTests</c> has no equivalent of
    /// (the grant direction carries no reason at all).</summary>
    [Fact]
    public async Task HandleAsync_OnSuccess_PassesTheTrimmedReasonAndRemovedByThrough()
    {
        var (handler, roles) = CreateFixture();
        roles.Seed(SiteId, "Admin", Guid.NewGuid(), [Permission.ChannelManage.Value]);

        var result = await handler.HandleAsync(
            Command(removedBy: "owner-sub-999", reason: "  never should have been granted  ", permissions: Permission.ChannelManage.Value),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var recorded = Assert.Single(roles.RemovalOverrides);
        Assert.Equal(SiteId, recorded.SiteId);
        Assert.Equal("Admin", recorded.RoleName);
        Assert.Equal([Permission.ChannelManage.Value], recorded.Permissions);
        Assert.Equal("owner-sub-999", recorded.RemovedBy);
        Assert.Equal("never should have been granted", recorded.Reason);
    }
}
