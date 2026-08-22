using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>Resolves a site from its public key - what the widget bootstrap handshake needs
/// (`1-06`); the public key is not a secret and grants nothing beyond starting a visitor session
/// (api-design.md).</summary>
public interface ISiteRepository
{
    Task<Site?> GetByPublicKeyAsync(string publicKey, CancellationToken cancellationToken);

    /// <summary>`5-01`: hub connections resolve their site from the JWT's `site_id` claim, never a
    /// public key - the layer-2 origin check (`VisitorHub`/`OperatorHub.OnConnectedAsync`) needs this
    /// site's own `AllowedOrigins`, and a public-key lookup has nothing to key off here.</summary>
    Task<Site?> GetByIdAsync(SiteId id, CancellationToken cancellationToken);

    /// <summary>`5-01`: does *any* site's `AllowedOrigins` contain this origin - the CORS
    /// preflight-time question, which cannot know *which* site a request is for (the body/token that
    /// would answer that has not arrived yet). Deliberately not "which site" - a caller needing that
    /// resolves the site through its own normal path (public key, JWT claim) once the real request
    /// arrives, and checks that specific site's `AllowedOrigins` itself.</summary>
    Task<bool> AnyAllowsOriginAsync(string origin, CancellationToken cancellationToken);
}
