using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetCannedResponses;
using Ago.Chat.Application.UseCases.UpdateCannedResponses;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.CannedResponses;

/// <summary>
/// `18-03`: `GET`/`PUT /api/v1/sites/{siteId}/canned-responses` - the same route shape, the same
/// `"RequireOperatorIdentity"` policy, and the same `site:configure` permission behind it
/// (checked in Application, never here - `adr/0016`) that `OfflineAutoReplyEndpoints` established for
/// a site-scoped, operator-only admin resource. `siteId` comes from the route rather than
/// <c>user.GetSiteId()</c>, for the identical reason that file states.
///
/// <para>The wire shape is flat <c>{title, body}</c> objects, deliberately not the Domain
/// <c>CannedResponse</c> record serialised directly - the same "a value object's constructor throwing
/// during model binding surfaces as an unusable 400" reasoning
/// <c>OfflineAutoReplyEndpoints</c> gives for its own rules.</para>
/// </summary>
public static class CannedResponseEndpoints
{
    public static void MapCannedResponseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/canned-responses")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleGetAsync);
        group.MapPut("", HandlePutAsync);
    }

    private static async Task<IResult> HandleGetAsync(
        Guid siteId, GetCannedResponsesHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetCannedResponses(new SiteId(siteId), httpContext.User.GetOperatorId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static async Task<IResult> HandlePutAsync(
        Guid siteId,
        CannedResponsesRequest request,
        UpdateCannedResponsesHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var responses = (request.Responses ?? [])
            .Select(item => new UpdateCannedResponsesItem(item.Title ?? string.Empty, item.Body ?? string.Empty))
            .ToList();

        var result = await handler.HandleAsync(
            new UpdateCannedResponses(new SiteId(siteId), httpContext.User.GetOperatorId(), responses),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static CannedResponsesResponse ToResponse(IReadOnlyList<CannedResponse> responses) =>
        new([.. responses.Select(r => new CannedResponseDto(r.Title, r.Body))]);

    /// <summary>Every string is nullable on the request only because a client can omit it; the handler
    /// is what decides an empty title or body is an error, and says so with a code
    /// (api-design.md).</summary>
    public sealed record CannedResponsesRequest(IReadOnlyList<CannedResponseDto>? Responses);

    public sealed record CannedResponsesResponse(IReadOnlyList<CannedResponseDto> Responses);

    public sealed record CannedResponseDto(string? Title, string? Body);
}
