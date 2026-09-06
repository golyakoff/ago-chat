using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RemoveTeamMessage;

/// <summary>
/// `23-33`: gated on <see cref="Permission.SiteManageOperators"/>, checked fresh against the
/// *remover* for every call - never the stored <see cref="TeamMessage.AuthorIsAdmin"/> label on the
/// message being removed, which answers a different question ("was the author the tenant's admin the
/// day they sent this") than the one this handler asks ("does the caller hold that power right now").
/// Reusing the same permission `SendTeamMessageHandler`'s own label check reads, rather than minting a
/// dedicated <c>team_chat:moderate</c> permission: this codebase already treats
/// <see cref="Permission.SiteManageOperators"/> as the precise definition of "the account owner" (that
/// handler's own remarks state the full reasoning - grant permissions, configure the site, erase it),
/// and the backlog item's own Scope names the actor by that exact description ("the account owner can
/// remove a message"). A new permission would answer a question this codebase has already answered.
///
/// <para><b>An ordinary operator may not remove even their own message.</b> The backlog item leaves
/// this open ("may a person delete their own message?") and this item decides against it: removal
/// is described throughout as the account owner's own power, and Scope's own second bullet - "An
/// ordinary operator cannot remove anybody's message, including their own" - is the literal reading
/// taken here rather than the "almost every chat allows it" alternative the item's own Open questions
/// section raises. Self-delete needing no accountability record (open question two) is exactly the
/// asymmetry that would make it a *different* power from moderation, not a smaller version of the same
/// one - building both in one item would be two promises, the seam CLAUDE.md rule 15 asks to cut
/// along, so this item ships only the one its own Scope actually describes.</para>
/// </summary>
public sealed class RemoveTeamMessageHandler(
    ITeamChatRepository teamChat, IPermissionChecker permissions, IClock clock, IIdGenerator idGenerator)
{
    public async Task<Result<TeamMessage>> HandleAsync(RemoveTeamMessage command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteManageOperators, cancellationToken);
        if (!allowed)
        {
            return TeamChatErrors.Forbidden("Operator does not have permission to remove messages from this site's team chat.");
        }

        var message = await teamChat.GetByIdAsync(command.TeamMessageId, cancellationToken);
        if (message is null || message.SiteId != command.SiteId)
        {
            // The same "wrong site reads identically to no such row" info-hiding shape
            // DeleteAttachmentHandler already uses for the identical reason - an operator token
            // scoped to one tenant must never learn that a message exists on another one.
            return TeamChatErrors.NotFound($"Team message {command.TeamMessageId.Value} was not found for the site.");
        }

        if (message.RemovedAt is not null)
        {
            // Idempotent, deliberately - the same "tolerate already-gone" reasoning
            // DeleteAttachmentHandler's own remarks give for a retried delete (a double-click, or a
            // retry after a dropped response to an already-successful call): it must succeed quietly,
            // not surface as an error the console has no good way to explain, and it must not write a
            // second removal record for one already-removed message.
            return Result<TeamMessage>.Success(message);
        }

        var now = clock.UtcNow;
        message.Remove(now);
        await teamChat.RemoveAsync(message, command.RequestedBy, idGenerator.NewId(now), now, cancellationToken);

        return Result<TeamMessage>.Success(message);
    }
}
