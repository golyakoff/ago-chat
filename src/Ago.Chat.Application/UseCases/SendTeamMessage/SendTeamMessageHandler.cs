using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.SendTeamMessage;

/// <summary>
/// `23-32`: no <see cref="IPermissionChecker"/> gate on *whether* to accept the send - see
/// <see cref="Application.UseCases.SendTeamMessage.SendTeamMessage"/>'s own remarks for why every
/// operator of the site may post unconditionally. The one permission check this handler makes decides
/// a label, not an authorization outcome: whether the author currently holds
/// <see cref="Permission.SiteManageOperators"/> - the same permission
/// <c>RegisterSiteHandler.AdminRolePermissions</c> grants the seeded "Admin" role and nothing else -
/// stands in for "is this operator the tenant's owner" (the backlog item's own "which roles carry the
/// label is part of this item").
///
/// <para><b>Why <see cref="Permission.SiteManageOperators"/> and not a new
/// <c>Operator.IsAccountOwner</c> flag.</b> `ago-chat` has no domain concept of a single distinguished
/// account owner today - `RegisterSiteHandler` grants the registering user both the "Operator" and
/// "Admin" roles, and a later invite can grant "Admin" to any number of other operators, so there is
/// no one row that is uniquely "the owner" to point at. The backlog item's own Goal names the person
/// this label is for by what they can *do* - "grant permissions, buy modules and close the account" -
/// which is exactly <see cref="Permission.SiteManageOperators"/>'s, <c>Permission.SiteConfigure</c>'s
/// and <c>Permission.SiteErase</c>'s own blast radius, all three Admin-role-only
/// (<see cref="Permission.SiteErase"/>'s own remarks). Checking one of that trio needs no new domain
/// state and no migration; inventing a first-operator-only flag (the shape `ago-calendar`'s
/// <c>adr/0083</c> chose for its own, different product) would add a second, competing notion of
/// "owner" to a codebase that has never needed one, to answer a question this permission already
/// answers. The trade this makes explicit: a site with more than one Admin labels every one of them,
/// not only whoever registered first - true to what each of them can actually do, and the reading
/// this item's own singular "the owner" prose least supports only when a tenant has deliberately
/// promoted a second person to its own most powerful role.</para>
/// </summary>
public sealed class SendTeamMessageHandler(
    ITeamChatRepository teamChat, IPermissionChecker permissions, IClock clock, IIdGenerator idGenerator)
{
    public async Task<Result<TeamMessage>> HandleAsync(SendTeamMessage command, CancellationToken cancellationToken)
    {
        MessageBody body;
        try
        {
            body = new MessageBody(command.Body);
        }
        catch (ArgumentException ex)
        {
            return TeamChatErrors.InvalidBody(ex.Message);
        }

        var authorIsAdmin = await permissions.HasPermissionAsync(
            command.AuthorId, command.SiteId, Permission.SiteManageOperators, cancellationToken);

        var now = clock.UtcNow;
        var id = new TeamMessageId(idGenerator.NewId(now));

        var message = await teamChat.PostAsync(
            command.SiteId, command.AuthorId, authorIsAdmin, body, command.ClientMessageId, id, now, cancellationToken);

        return Result<TeamMessage>.Success(message);
    }
}
