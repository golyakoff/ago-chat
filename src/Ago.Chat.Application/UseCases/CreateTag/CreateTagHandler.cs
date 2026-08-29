using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.CreateTag;

/// <summary>`18-04`: the tag vocabulary's own write, gated by <see cref="Permission.SiteConfigure"/> -
/// see <see cref="Permission.ConversationTag"/>'s own remarks on why creating/renaming/deleting a tag
/// is a site-configuration change while applying an existing tag to one conversation is not.</summary>
public sealed class CreateTagHandler(
    ITagRepository tags, IPermissionChecker permissions, IIdGenerator idGenerator, IClock clock)
{
    public async Task<Result<TagDto>> HandleAsync(CreateTag command, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            command.RequestedBy, command.SiteId, Permission.SiteConfigure, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to manage tags for this site.");
        }

        var trimmedName = command.Name.Trim();
        var existing = await tags.GetByNameAsync(command.SiteId, trimmedName, cancellationToken);
        if (existing is not null)
        {
            return ConversationErrors.TagAlreadyExists(trimmedName);
        }

        var now = clock.UtcNow;
        Tag tag;
        try
        {
            tag = Tag.Create(new TagId(idGenerator.NewId(now)), command.SiteId, command.Name, now);
        }
        catch (ArgumentException ex)
        {
            return ConversationErrors.TagInvalid(ex.Message);
        }

        // TagNameConflictException: the database's own unique-index enforcement of the same check
        // above, for the rare genuine race - TagNameConflictException's own remarks.
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
