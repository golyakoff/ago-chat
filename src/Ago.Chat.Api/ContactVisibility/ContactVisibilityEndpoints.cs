using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetContactVisibility;
using Ago.Chat.Application.UseCases.UpdateContactVisibility;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.ContactVisibility;

/// <summary>
/// `23-11`/`decisions.md` §5's amendment: `GET`/`PUT /api/v1/sites/{siteId}/contact-visibility` - the
/// same route shape, the same `"RequireOperatorIdentity"` policy, and the same `site:configure`
/// permission behind it (checked in Application, never here - `adr/0016`) `AssignmentPenaltyEndpoints`
/// already established for a site-scoped, operator-only admin resource. `siteId` comes from the route
/// rather than `user.GetSiteId()`, for the identical reason that file states: an operator's own site
/// claim is not necessarily the site being configured (`13-07`'s multi-tenancy).
/// </summary>
public static class ContactVisibilityEndpoints
{
    public static void MapContactVisibilityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/sites/{siteId:guid}/contact-visibility")
            .RequireAuthorization("RequireOperatorIdentity");

        group.MapGet("", HandleGetAsync);
        group.MapPut("", HandlePutAsync);
    }

    private static async Task<IResult> HandleGetAsync(
        Guid siteId, GetContactVisibilityHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new GetContactVisibility(new SiteId(siteId), httpContext.User.GetOperatorId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static async Task<IResult> HandlePutAsync(
        Guid siteId,
        ContactVisibilityRequest request,
        UpdateContactVisibilityHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new UpdateContactVisibility(new SiteId(siteId), httpContext.User.GetOperatorId(), request.Rung ?? string.Empty),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    private static ContactVisibilityResponse ToResponse(Domain.ContactVisibility rung) => new(rung.ToString());

    /// <summary><paramref name="Rung"/> is one of <c>"Visible"</c>/<c>"MaskedWithReveal"</c> -
    /// never <c>"Never"</c>, which does not parse to any member of <see cref="Domain.ContactVisibility"/>
    /// and is refused with the identical error any other unrecognised string gets
    /// (<c>UpdateContactVisibilityHandler</c>'s own remarks: absent from the type, not filtered out of
    /// it).</summary>
    public sealed record ContactVisibilityRequest(string? Rung);

    public sealed record ContactVisibilityResponse(string Rung);
}
