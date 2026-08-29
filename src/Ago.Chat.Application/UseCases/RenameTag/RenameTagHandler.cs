using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateTag;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.RenameTag;

/// <summary>`18-04`: same permission and duplicate-name reasoning as <see cref="CreateTag.CreateTagHandler"/> -
/// renaming to a name another tag on this site already holds is the same conflict, checked the same
/// way.</summary>
public sealed class RenameTagHandler(ITagRepository tags, IPermissionChecker permissions)
{
    public async Task<Result<TagDto>> HandleAsync(RenameTag command, CancellationToken cancellationToken)
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

        var trimmedName = command.Name.Trim();
        var existing = await tags.GetByNameAsync(command.SiteId, trimmedName, cancellationToken);
        if (existing is not null && existing.Id != tag.Id)
        {
            return ConversationErrors.TagAlreadyExists(trimmedName);
        }

        try
        {
            tag.Rename(command.Name);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.TagInvalid(ex.Message);
        }

        try
        {
            await tags.SaveAsync(tag, cancellationToken);
        }
        catch (TagNameConflictException)
        {
            return ConversationErrors.TagAlreadyExists(trimmedName);
        }

        return new TagDto(tag.Id.Value, tag.Name, tag.CreatedAt);
    }
}
