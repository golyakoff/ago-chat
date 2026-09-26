using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.RegisterOperatorDevice;
using Ago.Chat.Application.UseCases.RevokeOperatorDevice;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Me;

/// <summary>
/// `26-03`/`adr/0179` §1: the backend half of push device registration - see
/// `docs/architecture/push-notifications.md`'s own "Device registration" section for the full design.
/// `RequireOperatorIdentity` throughout, the same policy `WebhookEndpoints`' admin-only routes already
/// apply, and for a related but distinct reason: those routes manage a *site's* resource and check
/// `Permission.WebhookManage`; these routes manage the *caller's own* device row and need no further
/// permission check at all - `GetOperatorId()`/`GetSiteId()` name exactly the row a caller may touch,
/// the same self-scoped shape `MarkConversationReadHandler`'s own caller-is-the-subject routes use.
///
/// <para>Nothing here ever talks to FCM (`26-04`'s own scope) - a write to `operator_devices` and
/// nothing else, matching `docs/architecture/push-notifications.md`'s own "Which hosts change" table:
/// `Ago.Chat.Api` gains these two routes and never holds the push credential.</para>
/// </summary>
public static class MeDeviceEndpoints
{
    public static void MapMeDeviceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/me/devices").RequireAuthorization("RequireOperatorIdentity");

        group.MapPut("/{installationId}", HandleRegisterAsync);
        group.MapDelete("/{installationId}", HandleRevokeAsync);
    }

    private static async Task<IResult> HandleRegisterAsync(
        string installationId,
        RegisterDeviceRequest request,
        RegisterOperatorDeviceHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // A body carrying an unknown provider string is the caller's own mistake, not something
        // OperatorDevice.Register/Refresh should have to reject - the same "a route-segment/body value
        // that fails to parse is a 400, not a 500 from a Domain exception" split DocumentEndpoints' own
        // AcceptanceSubjectKind parse already draws.
        if (!Enum.TryParse<PushProvider>(request.Provider, ignoreCase: true, out var provider))
        {
            return Results.Problem(
                title: "OperatorDevice.InvalidProvider",
                detail: $"'{request.Provider}' is not a known push provider.",
                statusCode: StatusCodes.Status400BadRequest,
                type: "OperatorDevice.InvalidProvider");
        }

        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RegisterOperatorDevice(
                user.GetOperatorId(), user.GetSiteId(), installationId, provider, request.Platform, request.Token,
                request.DeviceId),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    private static async Task<IResult> HandleRevokeAsync(
        string installationId, RevokeOperatorDeviceHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new RevokeOperatorDevice(httpContext.User.GetOperatorId(), installationId), cancellationToken);

        // Always NoContent - RevokeOperatorDeviceHandler never returns a failure (Revoke's own
        // idempotence, and DELETE's own "no such row" case both fold into Result.Success()); this
        // mirrors the shape regardless, the same way HandleRevokeAsync in WebhookEndpoints does, in
        // case a future failure path is added here without this line being remembered.
        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    /// <summary>`26-122`: <see cref="DeviceId"/> is nullable on the wire, not required - a request that
    /// omits it (a client not yet updated) degrades gracefully to the pre-`26-122` installation-keyed
    /// upsert (`RegisterOperatorDeviceHandler`'s own remarks) rather than a 400; the shipped Android
    /// client always sends one.</summary>
    public sealed record RegisterDeviceRequest(string Provider, string Platform, string Token, string? DeviceId = null);
}
