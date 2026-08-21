using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.AssignConversation;

public sealed class AssignConversationHandler(
    IConversationRepository conversations,
    IPermissionChecker permissions,
    IClock clock)
{
    public async Task<Result> HandleAsync(AssignConversation command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.OperatorId, command.SiteId, Permission.ConversationAssign, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to claim conversations for this site.");
        }

        var conversation = await conversations.GetByIdAsync(command.ConversationId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        try
        {
            conversation.AssignTo(command.OperatorId, clock.UtcNow);
        }
        catch (InvalidConversationStateException ex)
        {
            return ConversationErrors.InvalidState(ex.Message);
        }

        await conversations.SaveAsync(conversation, cancellationToken);
        return Result.Success();
    }
}
