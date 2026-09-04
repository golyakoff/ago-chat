using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetMyPermissions;
using Ago.Chat.Application.UseCases.GetSeatAssignmentSummary;
using Ago.Chat.Application.UseCases.RemoveOperator;
using Ago.Chat.Application.UseCases.ToggleOperatorSeat;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Operators;

/// <summary>
/// `5-08`: a new endpoint group, not an addition to an existing one - `AttachmentEndpoints`/
/// `ConversationsEndpoints` both map routes under an existing plural resource this item extends;
/// `operators/me` is the first route about the operator resource itself, so it gets its own file
/// rather than being wedged into either.
///
/// <para>`13-03`: three more routes, all site-scoped and gated on `Permission.SiteManageOperators` -
/// the seat-assignment and operator-removal mechanism `13-01` named but did not build. Live here rather
/// than a new file, unlike `13-01`'s own `OperatorInviteEndpoints` split - those two routes have
/// genuinely different auth shapes (`RequireOperatorIdentity` vs `RequireKeycloakIdentity`); these
/// three and `operators/me` share the identical one.</para>
/// </summary>
public static class OperatorsEndpoints
{
    public static void MapOperatorsEndpoints(this WebApplication app)
    {
        // `5-08`: closes the gap `GetMyPermissionsHandler`'s own doc comment describes - the console
        // has no other way to learn which permissions the signed-in operator holds. Operator-only by
        // construction (a visitor has no permissions to ask about, `Visitor` "stays outside the role
        // system" per adr/0016), same "RequireOperatorIdentity" named policy every other operator-only
        // route in this file's siblings already uses.
        app.MapGet("/api/v1/operators/me", HandleGetMyPermissionsAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        app.MapPost("/api/v1/sites/{siteId:guid}/operators/{operatorId:guid}/seat", HandleToggleOperatorSeatAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        app.MapPost("/api/v1/sites/{siteId:guid}/operators/{operatorId:guid}/remove", HandleRemoveOperatorAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        app.MapGet("/api/v1/sites/{siteId:guid}/operators/seat-assignment-summary", HandleGetSeatAssignmentSummaryAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    private static async Task<IResult> HandleGetMyPermissionsAsync(
        GetMyPermissionsHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        // `23-02`: the token's own `name`/`email` claims - the console already requests the `openid
        // profile email` scope (`decisions.md` §1's own "found while deciding"), so every real sign-in
        // carries both. Read here, not resolved by `OperatorIdentityClaimsTransformation`: that class
        // must stay a pure read (this item's own Scope), and these two values exist only to be written
        // by the handler this call reaches, not to gate anything.
        var name = user.FindFirstValue(JwtRegisteredClaimNames.Name);
        var email = user.FindFirstValue(JwtRegisteredClaimNames.Email);
        var result = await handler.HandleAsync(
            new GetMyPermissions(user.GetOperatorId(), user.GetSiteId(), name, email), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    private static async Task<IResult> HandleToggleOperatorSeatAsync(
        Guid siteId,
        Guid operatorId,
        ToggleOperatorSeatRequest request,
        ToggleOperatorSeatHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new ToggleOperatorSeat(user.GetOperatorId(), new SiteId(siteId), new OperatorId(operatorId), request.HoldsSeat),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    private static async Task<IResult> HandleRemoveOperatorAsync(
        Guid siteId,
        Guid operatorId,
        RemoveOperatorHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RemoveOperator(user.GetOperatorId(), new SiteId(siteId), new OperatorId(operatorId)), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    private static async Task<IResult> HandleGetSeatAssignmentSummaryAsync(
        Guid siteId, GetSeatAssignmentSummaryHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(new GetSeatAssignmentSummary(user.GetOperatorId(), new SiteId(siteId)), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }

    public sealed record ToggleOperatorSeatRequest(bool HoldsSeat);
}
