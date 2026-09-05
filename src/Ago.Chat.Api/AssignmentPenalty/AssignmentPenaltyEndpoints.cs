using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetAssignmentPenalty;
using Ago.Chat.Application.UseCases.UpdateAssignmentPenalty;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.AssignmentPenalty;

/// <summary>
/// `23-05`: `GET`/`PUT /api/v1/sites/{siteId}/assignment-penalty` - the same route shape, the same
/// `"RequireOperatorIdentity"` policy, and the same `site:configure` permission behind it (checked in
/// Application, never here - `adr/0016`) `OfflineAutoReplyEndpoints` already established for a
/// site-scoped, operator-only admin resource. `siteId` comes from the route rather than
/// `user.GetSiteId()`, for the identical reason that file states: an operator's own site claim is not
/// necessarily the site being configured.
/// </summary>
public static class AssignmentPenaltyEndpoints
{
    public static void MapAssignmentPenaltyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/assignment-penalty")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleGetAsync);
        group.MapPut("", HandlePutAsync);
    }

    private static async Task<IResult> HandleGetAsync(
        Guid siteId, GetAssignmentPenaltyHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetAssignmentPenalty(new SiteId(siteId), httpContext.User.GetOperatorId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static async Task<IResult> HandlePutAsync(
        Guid siteId,
        AssignmentPenaltyRequest request,
        UpdateAssignmentPenaltyHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new UpdateAssignmentPenalty(new SiteId(siteId), httpContext.User.GetOperatorId(), request.PenaltySeconds),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static AssignmentPenaltyResponse ToResponse(int penaltySeconds) => new(penaltySeconds);

    public sealed record AssignmentPenaltyRequest(int PenaltySeconds);

    public sealed record AssignmentPenaltyResponse(int PenaltySeconds);
}
