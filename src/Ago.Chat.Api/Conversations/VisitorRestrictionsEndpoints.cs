using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetVisitorRestrictionsForSite;
using Ago.Chat.Application.UseCases.LiftVisitorRestriction;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Conversations;

/// <summary>
/// `23-69`/`23-77`: the shared visitor-restriction mechanism's own tenant-facing surface - the one
/// screen both items' own Done-when ask for ("the tenant can see how many, by whom" / "reversible...
/// scoped to one site"). Own <c>Map</c> call, not folded into <c>MapConversationsEndpoints</c> - the
/// same reason <c>SitesEndpoints</c>' own <c>MapAccessRecordsEndpoint</c> is separate from
/// <c>MapSitesEndpoints</c>: a test host that maps only the conversation endpoints must never be made
/// to resolve <see cref="GetVisitorRestrictionsForSiteHandler"/>/<see cref="LiftVisitorRestrictionHandler"/>
/// just because this file happened to sit next to <c>ConversationsEndpoints.cs</c>.
/// </summary>
public static class VisitorRestrictionsEndpoints
{
    public static void MapVisitorRestrictionsEndpoints(this WebApplication app)
    {
        // `GET /api/v1/visitor-restrictions` - `SiteId` from the operator's own token claims, the same
        // shape every other tenant-scoped read in `ConversationsEndpoints` already uses (`/all`,
        // `/queue`), not a `{siteId}` route segment the way `SitesEndpoints`' own `/access-records`
        // takes it - this screen has no reason to differ from its closer sibling, the conversation
        // list it sits next to in the console's own nav.
        app.MapGet("/api/v1/visitor-restrictions", HandleListAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        // `POST /api/v1/visitor-restrictions/{visitorId}/lift` - addressed by visitor, not by
        // conversation (`LiftVisitorRestriction`'s own remarks on why). `204`, the same "nothing to
        // return, the caller already knows what it asked for" contract `/close` already uses - unlike
        // `/close-as-spam`/`/block-visitor`, lifting computes nothing the caller does not already know.
        app.MapPost("/api/v1/visitor-restrictions/{visitorId:guid}/lift", HandleLiftAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    /// <summary>The wire shape of one page of restrictions - every field
    /// <see cref="VisitorRestrictionItem"/> carries, `Kind`/timestamps as the console needs to render
    /// them.</summary>
    /// <param name="EmojiCreature">`26-202`: <see cref="VisitorRestrictionItem.EmojiCreature"/>, so a
    /// client can show a name beside the restricted visitor's id the same way it already can for a
    /// conversation row or a person profile - additive/nullable the identical way every field above
    /// already is.</param>
    /// <param name="EmojiFood">The other half of the pair - see <paramref name="EmojiCreature"/>'s own
    /// remarks, which this parameter shares in full.</param>
    public sealed record VisitorRestrictionListItemDto(
        Guid Id,
        Guid VisitorId,
        string Kind,
        DateTimeOffset RestrictedAt,
        Guid RestrictedBy,
        DateTimeOffset? ExpiresAt,
        Guid SourceConversationId,
        DateTimeOffset? LiftedAt,
        Guid? LiftedBy,
        string? EmojiCreature = null,
        string? EmojiFood = null);

    public sealed record VisitorRestrictionListResponse(IReadOnlyList<VisitorRestrictionListItemDto> Items, Guid? NextBeforeId);

    private static async Task<IResult> HandleListAsync(
        Guid? before,
        int? limit,
        GetVisitorRestrictionsForSiteHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetVisitorRestrictionsForSite(user.GetSiteId(), user.GetOperatorId(), before, limit), cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        var page = result.Value;
        var items = page.Items
            .Select(i => new VisitorRestrictionListItemDto(
                i.Id, i.VisitorId.Value, i.Kind.ToString(), i.RestrictedAt, i.RestrictedBy.Value, i.ExpiresAt,
                i.SourceConversationId.Value, i.LiftedAt, i.LiftedBy?.Value, i.EmojiCreature, i.EmojiFood))
            .ToList();

        return Results.Ok(new VisitorRestrictionListResponse(items, page.NextBeforeId));
    }

    private static async Task<IResult> HandleLiftAsync(
        Guid visitorId, LiftVisitorRestrictionHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new LiftVisitorRestriction(new VisitorId(visitorId), user.GetOperatorId(), user.GetSiteId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }
}
