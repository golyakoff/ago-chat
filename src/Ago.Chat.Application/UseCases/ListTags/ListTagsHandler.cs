using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CreateTag;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.ListTags;

/// <summary>`18-04`: gated by <see cref="Permission.ConversationRead"/>, not <see cref="Permission.SiteConfigure"/>
/// like the write trio (Create/Rename/Delete) - every operator who can see conversations needs to be
/// able to browse the tag vocabulary to filter the queue by it (`GetOperatorQueueHandler`'s own tag
/// filter) and to apply an existing tag to a conversation (`TagConversationHandler`); only *managing*
/// the vocabulary itself is admin-scoped.</summary>
public sealed class ListTagsHandler(ITagRepository tags, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<TagDto>>> HandleAsync(ListTags query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var items = await tags.GetAllForSiteAsync(query.SiteId, cancellationToken);

        return Result<IReadOnlyList<TagDto>>.Success(
            items.Select(t => new TagDto(t.Id.Value, t.Name, t.CreatedAt)).ToList());
    }
}
