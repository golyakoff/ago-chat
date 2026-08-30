using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.CreateTag;
using Ago.Chat.Application.UseCases.DeleteTag;
using Ago.Chat.Application.UseCases.GetConversationTags;
using Ago.Chat.Application.UseCases.ListTags;
using Ago.Chat.Application.UseCases.RenameTag;
using Ago.Chat.Application.UseCases.TagConversation;
using Ago.Chat.Application.UseCases.UntagConversation;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Tags;

/// <summary>
/// `18-04`: two route groups, matching the permission split `Permission.ConversationTag`'s own
/// remarks state - the tag *vocabulary* is a per-site resource (`site:configure`-gated, the same
/// route shape `CannedResponseEndpoints` uses for its own site-scoped settings), while applying an
/// existing tag to one conversation is a narrower, per-conversation action
/// (`conversation:tag`-gated, the same route shape `NoteEndpoints`/`ConversationsEndpoints` use for
/// conversation-scoped actions).
/// </summary>
public static class TagEndpoints
{
    public static void MapTagEndpoints(this WebApplication app)
    {
        var vocabulary = app.MapGroup("/api/v1/sites/{siteId:guid}/tags")
            .RequireAuthorization("RequireOperatorIdentity");

        vocabulary.MapGet("", HandleListAsync);
        vocabulary.MapPost("", HandleCreateAsync);
        vocabulary.MapPut("/{tagId:guid}", HandleRenameAsync);
        vocabulary.MapDelete("/{tagId:guid}", HandleDeleteAsync);

        var conversationTags = app.MapGroup("/api/v1/conversations/{conversationId:guid}/tags")
            .RequireAuthorization("RequireOperatorIdentity");

        conversationTags.MapGet("", HandleGetForConversationAsync);
        conversationTags.MapPost("/{tagId:guid}", HandleApplyAsync);
        conversationTags.MapDelete("/{tagId:guid}", HandleRemoveAsync);
    }

    private static async Task<IResult> HandleListAsync(
        Guid siteId, ListTagsHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new ListTags(new SiteId(siteId), httpContext.User.GetOperatorId()), cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new TagsResponse([.. result.Value.Select(ToDto)]));
    }

    private static async Task<IResult> HandleCreateAsync(
        Guid siteId, TagRequest request, CreateTagHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new CreateTag(new SiteId(siteId), httpContext.User.GetOperatorId(), request.Name ?? string.Empty), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToDto(result.Value));
    }

    private static async Task<IResult> HandleRenameAsync(
        Guid siteId, Guid tagId, TagRequest request, RenameTagHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new RenameTag(new SiteId(siteId), new TagId(tagId), httpContext.User.GetOperatorId(), request.Name ?? string.Empty),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToDto(result.Value));
    }

    private static async Task<IResult> HandleDeleteAsync(
        Guid siteId, Guid tagId, DeleteTagHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new DeleteTag(new SiteId(siteId), new TagId(tagId), httpContext.User.GetOperatorId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    private static async Task<IResult> HandleGetForConversationAsync(
        Guid conversationId, GetConversationTagsHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetConversationTags(new ConversationId(conversationId), user.GetSiteId(), user.GetOperatorId()), cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new ConversationTagsResponse([.. result.Value.Select(ToConversationTagDto)]));
    }

    private static async Task<IResult> HandleApplyAsync(
        Guid conversationId, Guid tagId, TagConversationHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new TagConversation(new ConversationId(conversationId), user.GetSiteId(), new TagId(tagId), user.GetOperatorId()),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    private static async Task<IResult> HandleRemoveAsync(
        Guid conversationId, Guid tagId, UntagConversationHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new UntagConversation(new ConversationId(conversationId), user.GetSiteId(), new TagId(tagId), user.GetOperatorId()),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    private static TagResponseDto ToDto(Ago.Chat.Application.UseCases.CreateTag.TagDto t) => new(t.Id, t.Name, t.CreatedAt);

    /// <summary>`19-02`: <see cref="ConversationTagDto.Source"/> passed straight through - already the
    /// CLR member name of <see cref="Domain.TagSource"/> (<c>ConversationTagDto</c>'s own remarks), so
    /// there is no second mapping step here for this endpoint's own console consumer to disagree with.
    /// </summary>
    private static ConversationTagResponseDto ToConversationTagDto(ConversationTagDto t) =>
        new(t.Id, t.Name, t.CreatedAt, t.Source);

    /// <summary>Nullable only because a client can omit it - the handler decides an empty/oversized
    /// name is an error.</summary>
    public sealed record TagRequest(string? Name);

    public sealed record TagResponseDto(Guid Id, string Name, DateTimeOffset CreatedAt);

    public sealed record TagsResponse(IReadOnlyList<TagResponseDto> Tags);

    /// <summary>`19-02`: the one response shape that carries <see cref="Source"/> - see
    /// <see cref="ConversationTagDto"/>'s own remarks for why this does not also apply to
    /// <see cref="TagResponseDto"/>/<see cref="TagsResponse"/> (the vocabulary endpoints).</summary>
    public sealed record ConversationTagResponseDto(Guid Id, string Name, DateTimeOffset CreatedAt, string Source);

    public sealed record ConversationTagsResponse(IReadOnlyList<ConversationTagResponseDto> Tags);
}
