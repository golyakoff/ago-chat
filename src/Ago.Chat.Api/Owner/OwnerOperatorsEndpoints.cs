using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.RestoreOperatorSeatAsOwner;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Api.Owner;

/// <summary>
/// `23-68`: the platform owner's own recovery write - "a locked-out tenant can be let back in without a
/// database" (`docs/backlog/23-68-*.md`). A deliberately separate route and file from
/// <see cref="Operators.OperatorsEndpoints"/>, the same "`/owner/` stays the platform owner's own
/// namespace, never blurred with a site-scoped operator route" discipline
/// <see cref="OwnerChannelIdentityEndpoints"/>'s own remarks state for itself - even though both
/// ultimately call the identical <see cref="Domain.Operator.ToggleSeat"/> domain method.
///
/// <para><b>Gated exclusively by <c>RequirePlatformOwner</c></b> - the entire access-control story,
/// the same single-gate shape every other owner surface in this codebase already uses: the handler this
/// route resolves calls no <see cref="Application.Abstractions.IPermissionChecker"/>, and could not
/// (<see cref="RestoreOperatorSeatAsOwnerHandler"/>'s own remarks for why), which is precisely why this
/// route must never be mapped with any weaker policy.</para>
///
/// <para><b>Restore-only, never a general seat toggle</b> - see
/// <see cref="RestoreOperatorSeatAsOwner"/>'s own remarks for why releasing a seat from this console is
/// deliberately not built here, so this recovery action does not quietly grow into the wider "act as a
/// tenant" capability `23-68`'s own "What this is not" forbids.</para>
/// </summary>
public static class OwnerOperatorsEndpoints
{
    public static void MapOwnerOperatorsEndpoints(this WebApplication app)
    {
        app.MapPost(
                "/api/v1/owner/sites/{siteId:guid}/operators/{operatorId:guid}/restore-seat",
                HandleRestoreSeatAsync)
            .RequireAuthorization("RequirePlatformOwner");
    }

    private static async Task<IResult> HandleRestoreSeatAsync(
        Guid siteId,
        Guid operatorId,
        // Nullable, unlike every other owner write body in this file - the ordinary case (restore
        // within the seat limit, this item's own headline scenario) needs no fields at all, and a
        // runbook operator typing a bare `curl -X POST .../restore-seat` with no body must not be
        // turned away by minimal API's own "a non-nullable body is required" default the way a `Force`/
        // `Reason` pair with defaults would otherwise still demand an empty `{}`. Missing means
        // "not forcing", the identical reading `RestoreOperatorSeatRequest`'s own defaults already give
        // an explicit empty body.
        RestoreOperatorSeatRequest? request,
        RestoreOperatorSeatAsOwnerHandler handler,
        IAccessRecordRepository accessRecords,
        IClock clock,
        IIdGenerator idGenerator,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        // `23-13`'s own precedent, restated for this override: the caller's identity reaches the
        // handler to be recorded on the override row when one is written, never to authorise -
        // RequirePlatformOwner on this route already decided that. Read directly off the validated
        // token's `sub`, the identical claim OwnerAccessRecorder reads a few lines below for the
        // unrelated access-record write - read again here, not threaded through, because this read
        // gates the call itself: a missing claim here means nothing was even attempted, a different
        // fact from "nothing was recorded" (OwnerModuleEndpoints.HandleRevokeAsync's own remarks state
        // the identical reasoning).
        var restoredBy = httpContext.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (string.IsNullOrEmpty(restoredBy))
        {
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Token carries no subject claim.");
        }

        var result = await handler.HandleAsync(
            new RestoreOperatorSeatAsOwner(
                new SiteId(siteId), new OperatorId(operatorId), restoredBy, request?.Force ?? false, request?.Reason),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.Error!.Value.ToProblem(httpContext);
        }

        // `24-12`: recorded on every success, whether or not the seat limit was overridden to reach it
        // - the seat-limit override itself is a narrower, second fact, attested separately by
        // IOperatorSeatRestoreOverrideRepository only when actually exercised
        // (RestoreOperatorSeatAsOwnerHandler's own remarks). resourceId is the target OperatorId - the
        // row this write acted on, the same "name the specific row, not just the site" shape
        // OwnerModuleEndpoints.HandleGrantAsync's own resourceId already establishes.
        await OwnerAccessRecorder.RecordAsync(
            httpContext, accessRecords, clock, idGenerator, AccessRecordKind.OwnerOperatorSeatRestore,
            new SiteId(siteId), AccessRecordResourceKind.Operator, operatorId, cancellationToken);

        return Results.Ok(new RestoreOperatorSeatResponse(result.Value.AlreadyHeldSeat, result.Value.OverrodeSeatLimit));
    }

    /// <summary>The body `POST .../operators/{operatorId}/restore-seat` takes - see
    /// <see cref="RestoreOperatorSeatAsOwner"/>'s own remarks for the full argument on why an override
    /// past the site's own seat limit needs both fields stated together, never one alone.</summary>
    public sealed record RestoreOperatorSeatRequest(bool Force = false, string? Reason = null);

    /// <summary>What actually happened - see <see cref="RestoreOperatorSeatOutcome"/>'s own remarks for
    /// why the console needs both flags to report the act accurately rather than a bare "ok".</summary>
    public sealed record RestoreOperatorSeatResponse(bool AlreadyHeldSeat, bool OverrodeSeatLimit);
}
