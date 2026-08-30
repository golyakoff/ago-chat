using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.UseCases.GetConversationTags;

/// <summary>`18-04`: the conversation detail panel's own read - every tag currently applied to one
/// conversation. Gated by <see cref="Permission.ConversationRead"/>, the same reasoning
/// <c>GetConversationNotesHandler</c>'s own remarks give.</summary>
public sealed class GetConversationTagsHandler(
    IConversationReadStore readStore, ITagRepository tags, IPermissionChecker permissions)
{
    public async Task<Result<IReadOnlyList<ConversationTagDto>>> HandleAsync(
        GetConversationTags query, CancellationToken cancellationToken)
    {
        var allowed = await permissions.HasPermissionAsync(
            query.RequestedBy, query.SiteId, Permission.ConversationRead, cancellationToken);
        if (!allowed)
        {
            return ConversationErrors.Forbidden("Operator does not have permission to read conversations for this site.");
        }

        var conversation = await readStore.GetByIdAsync(query.ConversationId, query.SiteId, cancellationToken);
        if (conversation is null)
        {
            return ConversationErrors.NotFound(query.ConversationId.Value);
        }

        var items = await tags.GetForConversationAsync(query.ConversationId, cancellationToken);

        return Result<IReadOnlyList<ConversationTagDto>>.Success(
            items.Select(t => new ConversationTagDto(t.Tag.Id.Value, t.Tag.Name, t.Tag.CreatedAt, t.Source.ToString())).ToList());
    }
}
