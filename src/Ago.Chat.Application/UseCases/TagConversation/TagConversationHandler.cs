using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.TagConversation;

/// <summary>`18-04`: gated by <see cref="Permission.ConversationTag"/> - see that permission's own
/// remarks for why applying an existing tag to one conversation is a narrower capability than
/// managing the tag vocabulary itself (<see cref="Permission.SiteConfigure"/>).</summary>
public sealed class TagConversationHandler(
    IConversationReadStore readStore, ITagRepository tags, IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(TagConversation command, CancellationToken cancellationToken)
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

        await tags.AddToConversationAsync(command.ConversationId, command.TagId, cancellationToken);

        return Result.Success();
    }
}
