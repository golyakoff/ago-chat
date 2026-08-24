namespace Ago.Chat.Application.UseCases.RegisterSite;

/// <summary>`10-02`: <see cref="ExternalSubjectId"/> comes from the caller's validated Keycloak
/// token (`sub`, `10-01`'s `RequireKeycloakIdentity` policy), never from the request body - identity
/// is a property of the authenticated caller, not user-supplied data, the same reason
/// <c>CreateAttachmentAsOperator</c> carries its `SiteId` from token claims rather than a request
/// field. <see cref="RequestIp"/> is the second rate-limit key (`10-01`'s own Scope: "keyed per-`sub`
/// and per-IP") - `Application` may not reference `HttpContext`, so the endpoint reads it and passes
/// it through as a plain string, the same shape every other command in this codebase already uses for
/// values a handler needs but must not resolve itself.</summary>
public sealed record RegisterSite(string ExternalSubjectId, string RequestIp, string SiteName, string InitialAllowedOrigin);

public sealed record RegisteredSite(Guid SiteId, Guid OperatorId);
