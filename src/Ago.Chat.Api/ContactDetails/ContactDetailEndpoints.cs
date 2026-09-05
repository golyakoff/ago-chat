using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases.DeleteVisitorContactDetail;
using Ago.Chat.Application.UseCases.ListVisitorContactDetails;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.ContactDetails;

/// <summary>
/// `14-14`/`23-09`/`adr/0079` section 6: `GET`/`POST /api/v1/conversations/{conversationId}/contact-details`
/// and `DELETE /api/v1/conversations/{conversationId}/contact-details/{id}` - the only HTTP surface
/// that reaches `IVisitorContactDetailRepository`.
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

        return Results.Ok(new ContactDetailDto(
            result.Value.Id, result.Value.Kind, result.Value.Value, result.Value.RecordedByOperatorId,
            result.Value.Source, result.Value.Verified, result.Value.RecordedAt));
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

    private static ContactDetailDto ToDto(VisitorContactDetailDto d) =>
        new(d.Id, d.Kind, d.Value, d.RecordedByOperatorId, d.Source, d.Verified, d.RecordedAt);

    /// <summary>Nullable only because a client can omit either field - the handler decides an empty
    /// value or an unrecognised kind is an error, the same "validate downstream, translate the throw"
    /// split `NoteEndpoints.AddNoteRequest`'s own remarks describe for itself.</summary>
    public sealed record RecordContactDetailRequest(string? Kind, string? Value);

    /// <summary>`23-09`: <paramref name="RecordedByOperatorId"/> is nullable, and <paramref name="Source"/>/
    /// <paramref name="Verified"/> are new wire fields - see <c>VisitorContactDetailDto</c>'s own
    /// remarks for why a null operator id is never rendered as an empty cell or a fabricated name.</summary>
    public sealed record ContactDetailDto(
        Guid Id, string Kind, string Value, Guid? RecordedByOperatorId, string Source, bool Verified,
        DateTimeOffset RecordedAt);

    public sealed record ContactDetailsResponse(IReadOnlyList<ContactDetailDto> ContactDetails);
}
