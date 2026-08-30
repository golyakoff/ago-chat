using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.ListChannelIdentitiesForVisitor;
using Ago.Chat.Application.UseCases.RequestChannelLinkFromConsole;
using Ago.Chat.Application.UseCases.UnlinkChannelIdentity;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.ChannelIdentities;

/// <summary>
/// `14-12`/`adr/0079`: the console's own verified-channel-identity-linking surface -
/// `POST .../channel-identities/link-requests` (console-initiated), `GET .../channel-identities` (the
/// `VisitorPanel` listing), and `POST /api/v1/sites/{siteId}/channel-identities/{id}/unlink`
/// (operator-gated unlink). Operator-only, the same <c>"RequireOperatorIdentity"</c> policy every
/// conversation-scoped route in this codebase already uses (<c>NoteEndpoints</c>'s own precedent) - the
/// platform owner's own unconditional unlink is a deliberately separate route
/// (<c>OwnerChannelIdentityEndpoints</c>), never this one.
/// </summary>
public static class ChannelIdentityEndpoints
{
    public static void MapChannelIdentityEndpoints(this WebApplication app)
    {
        var conversationGroup = app.MapGroup("/api/v1/conversations/{conversationId:guid}/channel-identities")
            .RequireAuthorization("RequireOperatorIdentity");

        conversationGroup.MapGet("", HandleListAsync);
        conversationGroup.MapPost("/link-requests", HandleRequestLinkAsync);

        var siteGroup = app.MapGroup("/api/v1/sites/{siteId:guid}/channel-identities")
            .RequireAuthorization("RequireOperatorIdentity");

        siteGroup.MapPost("/{channelIdentityId:guid}/unlink", HandleUnlinkAsync);
    }

    private static async Task<IResult> HandleListAsync(
        Guid conversationId,
        ListChannelIdentitiesForVisitorHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new ListChannelIdentitiesForVisitor(new ConversationId(conversationId), user.GetOperatorId(), user.GetSiteId()),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new ChannelIdentitiesResponse([.. result.Value.Select(ToDto)]));
    }

    private static async Task<IResult> HandleRequestLinkAsync(
        Guid conversationId,
        RequestChannelLinkRequest request,
        RequestChannelLinkFromConsoleHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RequestChannelLinkFromConsole(
                user.GetOperatorId(), user.GetSiteId(), new ConversationId(conversationId), request.Kind ?? string.Empty),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new RequestChannelLinkResponse(result.Value.Code, result.Value.ExpiresAt, result.Value.Kind.ToString()));
    }

    private static async Task<IResult> HandleUnlinkAsync(
        Guid siteId,
        Guid channelIdentityId,
        UnlinkChannelIdentityHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new UnlinkChannelIdentity(user.GetOperatorId(), new SiteId(siteId), new ChannelIdentityId(channelIdentityId)),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    private static ChannelIdentityDto ToDto(ChannelIdentitySummary s) =>
        new(s.ChannelIdentityId, s.Kind.ToString(), s.Address, s.FirstSeenAt, s.LastSeenAt);

    public sealed record RequestChannelLinkRequest(string? Kind);

    public sealed record RequestChannelLinkResponse(string Code, DateTimeOffset ExpiresAt, string Kind);

    public sealed record ChannelIdentityDto(
        Guid ChannelIdentityId, string Kind, string Address, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);

    public sealed record ChannelIdentitiesResponse(IReadOnlyList<ChannelIdentityDto> ChannelIdentities);
}
