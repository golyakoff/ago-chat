using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.DeleteTag;

/// <summary>`18-04`: removes the tag definition and, through the schema's own cascade, every
/// `conversation_tags` row naming it - see <see cref="ITagRepository.DeleteAsync"/>'s own
/// remarks.</summary>
public sealed class DeleteTagHandler(ITagRepository tags, IPermissionChecker permissions)
{
    public async Task<Result> HandleAsync(DeleteTag command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage tags for this site.");
        }

        var tag = await tags.GetByIdAsync(command.TagId, command.SiteId, cancellationToken);
        if (tag is null)
        {
            return ConversationErrors.TagNotFound(command.TagId.Value);
        }

        await tags.DeleteAsync(tag, cancellationToken);

        return Result.Success();
    }
}
