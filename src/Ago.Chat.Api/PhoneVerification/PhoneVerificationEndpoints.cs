using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.ConfirmPhoneVerification;
using Ago.Chat.Application.UseCases.InitiatePhoneVerification;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.PhoneVerification;

/// <summary>
/// `14-15`/`adr/0079`: `POST /api/v1/conversations/{conversationId}/phone-verifications` and
/// `POST /api/v1/conversations/{conversationId}/phone-verifications/{id}/confirm` - the only HTTP surface
/// that reaches <c>InitiatePhoneVerificationHandler</c>/<c>ConfirmPhoneVerificationHandler</c>.
///
/// <para><b>Mapped on <see cref="AuthorizationPolicies.EitherTokenKind"/>, the same dual-scheme group
/// <c>AttachmentEndpoints</c> uses, then narrowed to visitor-only inline - not a new, dedicated
/// visitor-only policy.</b> Both handlers this file calls expose only a
/// <c>HandleAsVisitorAsync</c> entry point (<c>InitiatePhoneVerificationHandler</c>'s own remarks on why
/// this item was scoped without an operator twin), so an operator-authenticated caller is refused here,
/// structurally, before either handler is ever reached - the identical "reject the third state, do not
/// silently misclassify it" discipline <see cref="AuthorizationPolicies.EitherTokenKind"/>'s own remarks
/// already describe for its own third state. Reusing that policy rather than adding a single-scheme one
/// keeps one authorization vocabulary for "a conversation-scoped route either kind of caller might in
/// principle reach", with the visitor-only narrowing expressed as an ordinary handler branch instead of a
/// second policy class only this file would ever use.</para>
/// </summary>
public static class PhoneVerificationEndpoints
{
    public static void MapPhoneVerificationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/conversations/{conversationId:guid}/phone-verifications")
            .RequireAuthorization(AuthorizationPolicies.EitherTokenKind);

        group.MapPost("", HandleInitiateAsync);
        group.MapPost("/{pendingPhoneVerificationId:guid}/confirm", HandleConfirmAsync);
    }

    // `ago-root#353`: public, not private - `AttachmentEndpoints.HandleCreateAsync`'s own reasoning:
    // a test can call this directly to prove the Retry-After header, no hosting pipeline needed.
    public static async Task<IResult> HandleInitiateAsync(
        Guid conversationId,
        InitiatePhoneVerificationRequest request,
        InitiatePhoneVerificationHandler handler,
        PhoneVerificationRateLimitOptions rateLimitOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        if (user.IsOperator())
        {
            return ConversationErrors.Forbidden("Only the visitor may request their own phone verification.")
                .ToProblem(httpContext);
        }

        var result = await handler.HandleAsVisitorAsync(
            new InitiatePhoneVerificationAsVisitor(
                new ConversationId(conversationId), user.GetVisitorId(), request.Phone ?? string.Empty),
            cancellationToken);

        if (result.IsFailure)
        {
            var error = result.Error!.Value;
            // `ago-root#353`: the phone/visitor/site buckets `InitiatePhoneVerificationHandler` checks
            // all share this one code - the slowest of the three is the safe conservative answer
            // regardless of which one denied this call. `PhoneVerification.LockedOut` is a distinct
            // code from a different handler (`ConfirmPhoneVerificationHandler`) and never reaches this
            // branch - see its own remarks (`ConversationErrors.PhoneVerificationLockedOut`) for why it
            // deliberately carries no Retry-After at all.
            var retryAfter = error.Code == "PhoneVerification.RateLimited"
                ? RateLimitRetryAfter.Conservative(
                    rateLimitOptions.PerPhoneRefillPerSecond,
                    rateLimitOptions.PerVisitorRefillPerSecond,
                    rateLimitOptions.PerSiteRefillPerSecond)
                : (TimeSpan?)null;
            return error.ToProblem(httpContext, retryAfter);
        }

        return Results.Created(
            $"/api/v1/conversations/{conversationId}/phone-verifications/{result.Value.PendingPhoneVerificationId}",
            new InitiatedPhoneVerificationResponse(
                result.Value.PendingPhoneVerificationId, result.Value.ExpiresAt, result.Value.DeliveryMethod));
    }

    private static async Task<IResult> HandleConfirmAsync(
        Guid conversationId,
        Guid pendingPhoneVerificationId,
        ConfirmPhoneVerificationRequest request,
        ConfirmPhoneVerificationHandler handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        if (user.IsOperator())
        {
            return ConversationErrors.Forbidden("Only the visitor may confirm their own phone verification.")
                .ToProblem(httpContext);
        }

        var result = await handler.HandleAsVisitorAsync(
            new ConfirmPhoneVerificationAsVisitor(
                new ConversationId(conversationId), user.GetVisitorId(),
                new PendingPhoneVerificationId(pendingPhoneVerificationId), request.Code ?? string.Empty),
            cancellationToken);

        return result.IsFailure
            ? result.Error!.Value.ToProblem(httpContext)
            : Results.Ok(new ConfirmedPhoneVerificationResponse(result.Value.ChannelIdentityId, result.Value.WasNewlyLinked));
    }

    /// <summary>Nullable only because a client can omit the field - the handler decides an empty or
    /// unparsable value is an error, the same "validate downstream, translate the throw" split
    /// `ContactDetailEndpoints.RecordContactDetailRequest`'s own remarks describe for itself.</summary>
    public sealed record InitiatePhoneVerificationRequest(string? Phone);

    public sealed record ConfirmPhoneVerificationRequest(string? Code);

    public sealed record InitiatedPhoneVerificationResponse(
        Guid PendingPhoneVerificationId, DateTimeOffset ExpiresAt, string DeliveryMethod);

    public sealed record ConfirmedPhoneVerificationResponse(Guid ChannelIdentityId, bool WasNewlyLinked);
}
