using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.GetMyPermissions;

namespace Ago.Chat.Api.Operators;

/// <summary>
/// `5-08`: a new endpoint group, not an addition to an existing one - `AttachmentEndpoints`/
/// `ConversationsEndpoints` both map routes under an existing plural resource this item extends;
/// `operators/me` is the first route about the operator resource itself, so it gets its own file
/// rather than being wedged into either.
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
    }

    private static async Task<IResult> HandleGetMyPermissionsAsync(
        GetMyPermissionsHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new GetMyPermissions(user.GetOperatorId(), user.GetSiteId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(result.Value);
    }
}
