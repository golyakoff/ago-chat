using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.UntagConversation;

/// <summary>`18-04`: the mirror of <c>TagConversationHandler</c> - same permission, same
/// existence/tenant checks. Idempotent at the repository (<see cref="ITagRepository.RemoveFromConversationAsync"/>'s
/// own remarks): removing a tag that was never applied is a no-op, not an error.
///
/// <para><b>`adr/0186` S1: <see cref="clock"/> joins this handler's dependencies</b> on the identical
/// terms <c>TagConversationHandler</c>'s own remarks give - only to hand a timestamp down to the
/// repository, which is the analytics <c>ConversationUntagged</c> event's one publisher.</para></summary>
public sealed class UntagConversationHandler(
    IConversationReadStore readStore, ITagRepository tags, IPermissionChecker permissions, IClock clock)
{
    public async Task<Result> HandleAsync(UntagConversation command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.ConversationTag, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to tag conversations for this site.");
        }

        var conversation = await readStore.GetByIdAsync(command.ConversationId, command.SiteId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(command.ConversationId.Value);
        }

        var tag = await tags.GetByIdAsync(command.TagId, command.SiteId, cancellationToken);
        if (tag is null)
        {
            return ConversationErrors.TagNotFound(command.TagId.Value);
        }

        await tags.RemoveFromConversationAsync(
            command.ConversationId, command.SiteId, command.TagId, clock.UtcNow, cancellationToken);

        return Result.Success();
    }
}
