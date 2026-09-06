using Ago.Chat.Api.Auth;
using Ago.Chat.Api.Http;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.GetConsentRequirement;
using Ago.Chat.Application.UseCases.RecordVisitorConsent;
using Ago.Chat.Domain;

namespace Ago.Chat.Api.Consent;

/// <summary>
/// `24-05`: `GET`/`POST /api/v1/conversations/{conversationId}/consent` - the only HTTP surface that
/// reaches <c>GetConsentRequirementHandler</c>/<c>RecordVisitorConsentHandler</c>.
///
/// <para><b>Mapped on <see cref="AuthorizationPolicies.EitherTokenKind"/>, then narrowed to
/// visitor-only inline - the identical shape <c>PhoneVerificationEndpoints</c> already establishes for
/// itself.</b> An operator may still need to <em>see</em> whether a site requires consent (the console's
/// widget-config screen already answers that, through <c>GetWidgetConfigHandler</c>'s own RBAC-gated
/// read - deliberately not duplicated here), but never to accept <em>on a visitor's behalf</em> - both
/// handlers this file calls expose only a visitor entry point, so an operator-authenticated caller is
/// refused here, before either handler is ever reached, the same reasoning
/// <c>PhoneVerificationEndpoints</c>'s own remarks give for its own dual-scheme group narrowed the
/// identical way.</para>
/// </summary>
public static class ConsentEndpoints
{
    public static void MapConsentEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/conversations/{conversationId:guid}/consent")
            .RequireAuthorization(AuthorizationPolicies.EitherTokenKind);

        group.MapGet("", HandleGetAsync);
        group.MapPost("", HandleRecordAsync);
    }

    // `ago-root#353`: public, not private - the same reasoning every other rate-limited endpoint in
    // this codebase already gives (`AttachmentEndpoints.HandleCreateAsync`'s own remarks): a test can
    // call this directly to prove the Retry-After header, no hosting pipeline needed.
    public static async Task<IResult> HandleGetAsync(
        Guid conversationId, GetConsentRequirementHandler handler, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        if (user.IsOperator())
        {
            return ConversationErrors.Forbidden("Only the visitor may read their own consent state.").ToProblem(httpContext);
        }

        var result = await handler.HandleAsync(
            new GetConsentRequirement(new ConversationId(conversationId), user.GetVisitorId()), cancellationToken);

        return result.IsFailure ? result.Error!.Value.ToProblem(httpContext) : Results.Ok(ToResponse(result.Value));
    }

    public static async Task<IResult> HandleRecordAsync(
        Guid conversationId,
        RecordConsentRequest request,
        RecordVisitorConsentHandler handler,
        ConsentRateLimitOptions rateLimitOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var user = httpContext.User;
        if (user.IsOperator())
        {
            return ConversationErrors.Forbidden("Only the visitor may accept their own consent.").ToProblem(httpContext);
        }

        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = httpContext.Request.Headers.UserAgent.ToString();
        userAgent = userAgent.Length switch
        {
            0 => null,
            > AcceptanceRecord.MaxUserAgentLength => userAgent[..AcceptanceRecord.MaxUserAgentLength],
            _ => userAgent,
        };

        var result = await handler.HandleAsync(
            new RecordVisitorConsent(
                new ConversationId(conversationId), user.GetVisitorId(), request.Purpose ?? string.Empty, clientIp, userAgent),
            cancellationToken);

        if (result.IsFailure)
        {
            var error = result.Error!.Value;
            var retryAfter = error.Code == "Consent.RateLimited"
                ? RateLimitRetryAfter.Conservative(rateLimitOptions.PerVisitorRefillPerSecond, rateLimitOptions.PerSiteRefillPerSecond)
                : (TimeSpan?)null;
            return error.ToProblem(httpContext, retryAfter);
        }

        return Results.Ok(new RecordedConsentResponse(
            result.Value.Id, result.Value.DocumentKey, result.Value.DocumentVersion, result.Value.AcceptedAt));
    }

    private static ConsentRequirementResponse ToResponse(ConsentRequirement requirement) =>
        new(
            requirement.ContactRequired,
            requirement.Contact is null ? null : ToDocumentResponse(requirement.Contact),
            requirement.ContactAlreadyAccepted,
            requirement.Marketing is null ? null : ToDocumentResponse(requirement.Marketing),
            requirement.MarketingAlreadyAccepted);

    private static ConsentDocumentResponse ToDocumentResponse(ConsentDocumentSummary summary) =>
        new(summary.DocumentKey, summary.Version, summary.Title, summary.Body, summary.PublishedAt);

    /// <summary>Nullable only because a client can omit it - the handler decides an empty or
    /// unparsable value is an error, the same "validate downstream, translate the throw" split
    /// every other raw-string request body in this codebase already follows.</summary>
    public sealed record RecordConsentRequest(string? Purpose);

    public sealed record ConsentDocumentResponse(string DocumentKey, string? Version, string? Title, string? Body, DateTimeOffset? PublishedAt);

    public sealed record ConsentRequirementResponse(
        bool ContactRequired, ConsentDocumentResponse? Contact, bool ContactAlreadyAccepted,
        ConsentDocumentResponse? Marketing, bool MarketingAlreadyAccepted);

    public sealed record RecordedConsentResponse(Guid Id, string DocumentKey, string DocumentVersion, DateTimeOffset AcceptedAt);
}
