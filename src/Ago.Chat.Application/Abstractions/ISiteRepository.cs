using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>Resolves a site from its public key - what the widget bootstrap handshake needs
/// (`1-06`); the public key is not a secret and grants nothing beyond starting a visitor session
/// (api-design.md).</summary>
public interface ISiteRepository
{
    Task<Site?> GetByPublicKeyAsync(string publicKey, CancellationToken cancellationToken);
}
