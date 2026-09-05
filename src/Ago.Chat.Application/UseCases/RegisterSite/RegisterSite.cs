using Ago.Chat.Domain;

namespace Ago.Chat.Application.UseCases.RegisterSite;

/// <summary>`10-02`: <see cref="ExternalSubjectId"/> comes from the caller's validated Keycloak
/// token (`sub`, `10-01`'s `RequireKeycloakIdentity` policy), never from the request body - identity
/// is a property of the authenticated caller, not user-supplied data, the same reason
/// <c>CreateAttachmentAsOperator</c> carries its `SiteId` from token claims rather than a request
/// field. <see cref="RequestIp"/> is the second rate-limit key (`10-01`'s own Scope: "keyed per-`sub`
/// and per-IP") - `Application` may not reference `HttpContext`, so the endpoint reads it and passes
/// it through as a plain string, the same shape every other command in this codebase already uses for
/// values a handler needs but must not resolve itself.</summary>
/// <summary>`23-02`: <paramref name="Name"/>/<paramref name="Email"/> are the same validated token's
/// own `name`/`email` claims, carried through for the identical reason
/// <paramref name="ExternalSubjectId"/> already is - this bootstrap endpoint creates an `Operator`
/// from a real human's token too, not only `13-01`'s invite redemption, so it must "pass whatever it
/// has" the same way (`23-02`'s own backlog note). Optional, appended at the end so every existing
/// caller keeps compiling.</summary>
/// <summary>`24-03`: <paramref name="UserAgent"/> is the request's own `User-Agent` header, carried
/// through for the same reason <paramref name="RequestIp"/> already is - both become
/// <see cref="AcceptanceRecord"/>'s own <c>ClientIp</c>/<c>UserAgent</c> request-context fields when
/// this registration records an acceptance, never resolved by the handler itself
/// (`Application` may not reference `HttpContext`). Nullable: a caller with no header to forward (an
/// internal call, a test) must not be forced to invent one - `AcceptanceRecord.Accept`'s own
/// remarks already accept a null `UserAgent`.</summary>
public sealed record RegisterSite(
    string ExternalSubjectId, string RequestIp, string SiteName, string InitialAllowedOrigin,
    string? Name = null, string? Email = null, string? UserAgent = null);

public sealed record RegisteredSite(Guid SiteId, Guid OperatorId);
