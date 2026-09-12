using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.DeleteVisitorContactDetail;
using Ago.Chat.Application.UseCases.EditVisitorContactDetail;
using Ago.Chat.Application.UseCases.ListVisitorContactDetails;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
using Ago.Chat.Application.UseCases.RevealVisitorContactDetail;
using Ago.Chat.Application.UseCases.SetVisitorContactDetailAssessment;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.ContactDetails;

/// <summary>
/// `14-14`/`23-09`/`adr/0079` section 6: `GET`/`POST /api/v1/conversations/{conversationId}/contact-details`,
/// `DELETE /api/v1/conversations/{conversationId}/contact-details/{id}`, and (`25-58`)
/// `PATCH .../{id}` (edit) plus `PATCH .../{id}/assessment` (confirm/mark invalid) - the only HTTP
/// surface that reaches `IVisitorContactDetailRepository`.
///
/// <para><b>`23-09`: `POST` is now dual-scheme, `GET`/`DELETE` stay operator-only - mapped directly on
/// `app`, not nested inside the dual-scheme group.</b> The same reasoning `AttachmentEndpoints`'s own
/// remarks give for its own single-scheme `DELETE`: stacking a second `RequireAuthorization` on top of
/// a group's own dual-scheme policy would combine (AND) both authentication requirements rather than
/// replace one, which is not what a single-scheme route wants. `HandleRecordAsync` below branches on
/// <see cref="ClaimsPrincipalExtensions.IsOperator"/> the identical way `AttachmentEndpoints.HandleCreateAsync`
/// does, since <c>RecordVisitorContactDetailHandler</c> now exposes two differently-shaped entry
/// points rather than one shared by both callers.</para>
/// </summary>
public static class ContactDetailEndpoints
{
    public static void MapContactDetailEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/v1/conversations/{conversationId:guid}/contact-details")
            .RequireAuthorization(AuthorizationPolicies.EitherTokenKind)
            .MapPost("", HandleRecordAsync);

        app.MapGet("/api/v1/conversations/{conversationId:guid}/contact-details", HandleListAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        app.MapDelete("/api/v1/conversations/{conversationId:guid}/contact-details/{contactDetailId:guid}", HandleDeleteAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        // `25-58`: real inline editing - an operator corrects an existing row's own value, never a
        // second, competing one. Operator-only, matching `DELETE`/`GET` above - there is no
        // visitor-facing edit (`EditVisitorContactDetail`'s own remarks).
        app.MapPatch("/api/v1/conversations/{conversationId:guid}/contact-details/{contactDetailId:guid}", HandleEditAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        // `25-58`: the confirm/mark-invalid action - Phone and Email only
        // (`SetVisitorContactDetailAssessmentHandler`'s own remarks on why a `Name` row is rejected
        // here, not silently accepted and ignored). Its own route, not folded into the edit `PATCH`
        // above - editing a value and asserting a judgment about it are two different writes with two
        // different failure shapes (an edit can fail on the value's own validity; an assessment can
        // fail on the row's own kind), the same "one route per distinct write" shape this file's
        // existing `POST`/`DELETE`/`reveal` split already follows.
        app.MapPatch(
                "/api/v1/conversations/{conversationId:guid}/contact-details/{contactDetailId:guid}/assessment",
                HandleSetAssessmentAsync)
            .RequireAuthorization("RequireOperatorIdentity");

        // `23-11`: same route family, own Map call on the same group's own file - not a separate
        // "own Map call" the way `SitesEndpoints.MapAccessRecordsEndpoint` needs (this codebase's own
        // trap, `24-12`'s own remarks: several stripped-down test hosts call `MapSitesEndpoints`
        // without registering that route's own handler). No stripped-down test host in this codebase
        // calls `MapContactDetailEndpoints` at all - it is reachable only through the real `Program`,
        // where every handler on this page is already registered - so this route can join the same
        // group with no such risk.
        app.MapPost(
                "/api/v1/conversations/{conversationId:guid}/contact-details/{contactDetailId:guid}/reveal",
                HandleRevealAsync)
            .RequireAuthorization("RequireOperatorIdentity");
    }

    private static async Task<IResult> HandleListAsync(
        Guid conversationId,
        ListVisitorContactDetailsHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new ListVisitorContactDetails(new ConversationId(conversationId), user.GetOperatorId(), user.GetSiteId()),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new ContactDetailsResponse([.. result.Value.Select(ToDto)]));
    }

    // `ago-root#353`: public, not private - `AttachmentEndpoints.HandleCreateAsync`'s own reasoning: a
    // test can call this directly to prove the Retry-After header, no hosting pipeline needed.
    public static async Task<IResult> HandleRecordAsync(
        Guid conversationId,
        RecordContactDetailRequest request,
        RecordVisitorContactDetailHandler handler,
        ContactDetailRateLimitOptions rateLimitOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var id = new ConversationId(conversationId);
        var kind = request.Kind ?? string.Empty;
        var value = request.Value ?? string.Empty;

        var result = user.IsOperator()
            ? await handler.HandleAsOperatorAsync(
                new RecordVisitorContactDetailAsOperator(user.GetOperatorId(), user.GetSiteId(), id, kind, value),
                cancellationToken)
            : await handler.HandleAsVisitorAsync(
                new RecordVisitorContactDetailAsVisitor(id, user.GetVisitorId(), kind, value), cancellationToken);

        if (result.IsFailure)
        {
            var error = result.Error!.Value;
            // Only the visitor path can ever return this code (the operator path has no rate limit of
            // its own, per `Permission.ConversationSend` already gating it) - the same
            // `error.Code == "*.RateLimited"` branch every rate-limited endpoint in this codebase uses.
            var retryAfter = error.Code == "Message.RateLimited"
                ? RateLimitRetryAfter.Conservative(rateLimitOptions.PerVisitorRefillPerSecond, rateLimitOptions.PerSiteRefillPerSecond)
                : (TimeSpan?)null;
            return error.ToProblem(httpContext, retryAfter);
        }

        // `23-11`: Masked is always false here - a caller who just submitted this value already knows
        // it, the identical "this is not a list read" reasoning that keeps a reveal's own response
        // unmasked (RevealVisitorContactDetailHandler's own remarks).
        return Results.Ok(new ContactDetailDto(
            result.Value.Id, result.Value.Kind, result.Value.Value, result.Value.RecordedByOperatorId,
            result.Value.Source, result.Value.Verified, result.Value.RecordedAt, Masked: false, result.Value.Assessment));
    }

    private static async Task<IResult> HandleDeleteAsync(
        Guid conversationId,
        Guid contactDetailId,
        DeleteVisitorContactDetailHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new DeleteVisitorContactDetail(
                user.GetOperatorId(), user.GetSiteId(), new ConversationId(conversationId),
                new VisitorContactDetailId(contactDetailId)),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.NoContent();
    }

    /// <summary>`25-58`: `PATCH .../contact-details/{contactDetailId}` - an operator's own correction to
    /// an existing row's own value. Reuses the list's own <see cref="ContactDetailDto"/> shape, the
    /// identical "not a parallel type" reasoning `HandleRevealAsync`'s own remarks give for
    /// itself.</summary>
    private static async Task<IResult> HandleEditAsync(
        Guid conversationId,
        Guid contactDetailId,
        EditContactDetailRequest request,
        EditVisitorContactDetailHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new EditVisitorContactDetail(
                user.GetOperatorId(), user.GetSiteId(), new ConversationId(conversationId),
                new VisitorContactDetailId(contactDetailId), request.Value ?? string.Empty),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToDto(result.Value));
    }

    /// <summary>`25-58`: `PATCH .../contact-details/{contactDetailId}/assessment` - an operator's own
    /// confirm/mark-invalid call, Phone and Email only.</summary>
    private static async Task<IResult> HandleSetAssessmentAsync(
        Guid conversationId,
        Guid contactDetailId,
        SetContactDetailAssessmentRequest request,
        SetVisitorContactDetailAssessmentHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new SetVisitorContactDetailAssessment(
                user.GetOperatorId(), user.GetSiteId(), new ConversationId(conversationId),
                new VisitorContactDetailId(contactDetailId), request.Assessment ?? string.Empty),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToDto(result.Value));
    }

    /// <summary>`23-11`: `POST .../contact-details/{contactDetailId}/reveal` - one contact detail,
    /// one reveal, one record (`RevealVisitorContactDetailHandler`'s own remarks). Reuses the list's
    /// own <see cref="ContactDetailDto"/> shape rather than a parallel type - a reveal's response is
    /// the identical row the list already renders, with <c>Masked</c> now <see langword="false"/> and
    /// <c>Value</c> now the real one.</summary>
    private static async Task<IResult> HandleRevealAsync(
        Guid conversationId,
        Guid contactDetailId,
        RevealVisitorContactDetailHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        var result = await handler.HandleAsync(
            new RevealVisitorContactDetail(
                new ConversationId(conversationId), new VisitorContactDetailId(contactDetailId),
                user.GetOperatorId(), user.GetSiteId()),
            cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToDto(result.Value));
    }

    private static ContactDetailDto ToDto(VisitorContactDetailDto d) =>
        new(d.Id, d.Kind, d.Value, d.RecordedByOperatorId, d.Source, d.Verified, d.RecordedAt, d.Masked, d.Assessment);

    /// <summary>Nullable only because a client can omit either field - the handler decides an empty
    /// value or an unrecognised kind is an error, the same "validate downstream, translate the throw"
    /// split `NoteEndpoints.AddNoteRequest`'s own remarks describe for itself.</summary>
    public sealed record RecordContactDetailRequest(string? Kind, string? Value);

    /// <summary>`25-58`: nullable for the same reason `RecordContactDetailRequest.Value` is - the
    /// handler, not this endpoint, decides an empty value is an error.</summary>
    public sealed record EditContactDetailRequest(string? Value);

    /// <summary>`25-58`: the wire name of a <see cref="Domain.VisitorContactDetailAssessment"/> member -
    /// `"Confirmed"` or `"Invalid"`, never `"Unset"` (the handler rejects that as not a settable
    /// target, the same rule `SetConversationOutcome`'s own wire string follows for
    /// <see cref="Domain.ConversationOutcome.Unset"/>).</summary>
    public sealed record SetContactDetailAssessmentRequest(string? Assessment);

    /// <summary>`23-09`: <paramref name="RecordedByOperatorId"/> is nullable, and <paramref name="Source"/>/
    /// <paramref name="Verified"/> are new wire fields - see <c>VisitorContactDetailDto</c>'s own
    /// remarks for why a null operator id is never rendered as an empty cell or a fabricated name.
    ///
    /// <para>`23-11`: <paramref name="Masked"/> - see <c>VisitorContactDetailDto</c>'s own remarks.
    /// On the list read, <see langword="true"/> means <paramref name="Value"/> is a masked string and
    /// the console should offer a reveal action; on the record/reveal responses it is always
    /// <see langword="false"/>, since a caller of either already has the real value in hand.</para>
    ///
    /// <para>`25-58`: <paramref name="Assessment"/> - see <c>VisitorContactDetailDto</c>'s own remarks on
    /// why this is never <paramref name="Verified"/> restated under a new name.</para></summary>
    public sealed record ContactDetailDto(
        Guid Id, string Kind, string Value, Guid? RecordedByOperatorId, string Source, bool Verified,
        DateTimeOffset RecordedAt, bool Masked, string Assessment);

    public sealed record ContactDetailsResponse(IReadOnlyList<ContactDetailDto> ContactDetails);
}
