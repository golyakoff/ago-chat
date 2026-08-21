using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Api.Auth;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        // The real mechanism: a visitor's own token-issuance path, not a stub. Anyone who knows a
        // site's public key (not a secret - api-design.md) can start a session; nothing sensitive is
        // granted until the visitor sends a message, at which point Conversation.AddVisitorMessage
        // (1-01) checks it is this same visitor.
        app.MapPost("/api/v1/visitor-sessions", async (
            VisitorSessionRequest request,
            ISiteRepository sites,
            IIdGenerator idGenerator,
            IClock clock,
            JwtTokenService tokens,
            CancellationToken cancellationToken) =>
        {
            var site = await sites.GetByPublicKeyAsync(request.PublicKey, cancellationToken);
            if (site is null)
            {
                return Results.Problem(
                    title: "Site not found", statusCode: StatusCodes.Status404NotFound, type: "site-not-found");
            }

            var visitorId = new VisitorId(idGenerator.NewId(clock.UtcNow));
            var token = tokens.IssueVisitorToken(visitorId, site.Id);
            return Results.Created(
                $"/api/v1/visitor-sessions/{visitorId.Value}",
                new VisitorSessionResponse(token, visitorId.Value));
        });

        // Dev-only: trades an operator id for a session token directly, no password, no check that
        // the id is real - IPermissionChecker (adr/0016) is what actually gates anything this token
        // is used for. Mapped only in Development, never reachable otherwise, and replaced outright
        // by OIDC at Stage 5, not evolved into it (authorization.md).
        if (app.Environment.IsDevelopment())
        {
            app.MapPost("/dev/operator-session", (OperatorSessionRequest request, JwtTokenService tokens) =>
            {
                var token = tokens.IssueOperatorToken(new OperatorId(request.OperatorId), new SiteId(request.SiteId));
                return Results.Ok(new OperatorSessionResponse(token));
            });
        }
    }

    public sealed record VisitorSessionRequest(string PublicKey);

    public sealed record VisitorSessionResponse(string Token, Guid VisitorId);

    public sealed record OperatorSessionRequest(Guid OperatorId, Guid SiteId);

    public sealed record OperatorSessionResponse(string Token);
}
