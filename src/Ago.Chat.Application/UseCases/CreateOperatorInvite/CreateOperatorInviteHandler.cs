using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.CreateOperatorInvite;

/// <summary>
/// `13-01`: `Permission.SiteManageOperators`'s first real write-path caller -
/// `docs/architecture/authorization.md` already noted "no handler anywhere uses it beyond the admin
/// console's read-only view" before this item. Generation is the simple half of this item (an ordinary
/// single-aggregate insert); the seat-limit enforcement this invite exists to gate lives entirely in
/// `RedeemOperatorInviteHandler`/`OperatorInviteRedemptionRepository` instead, at redemption time, per
/// this item's own Goal ("the entitlement check is enforced at its one real write path - operator
/// invite redemption - not bolted onto `10-02`'s registration flow").
/// </summary>
public sealed class CreateOperatorInviteHandler(
    IOperatorInviteRepository invites,
    IRoleRepository roles,
    IPermissionChecker permissions,
    IOperatorInviteCodeGenerator codeGenerator,
    OperatorInviteOptions options,
    IIdGenerator idGenerator,
    IClock clock)
{
    public async Task<Result<CreatedOperatorInvite>> HandleAsync(CreateOperatorInvite command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteManageOperators, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage operators for this site.");
        }

        var roleId = await roles.GetIdByNameAsync(command.SiteId, command.RoleName, cancellationToken);
        if (roleId is null)
        {
            return ConversationErrors.OperatorInviteInvalidRole(
                $"Site {command.SiteId.Value} has no role named '{command.RoleName}'.");
        }

        var now = clock.UtcNow;
        var id = new OperatorInviteId(idGenerator.NewId(now));

        var code = codeGenerator.NewCode();
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes(code));

        var invite = OperatorInvite.Generate(
            id, command.SiteId, roleId.Value, codeHash, command.RequestedBy, now, options.ValidFor);
        await invites.SaveAsync(invite, cancellationToken);

        return new CreatedOperatorInvite(id.Value, code, invite.ExpiresAt);
    }
}
