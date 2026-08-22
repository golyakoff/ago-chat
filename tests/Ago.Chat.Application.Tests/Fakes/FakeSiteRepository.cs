using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeSiteRepository : ISiteRepository
{
    private readonly Dictionary<string, Site> _byPublicKey = [];

    public int LookupCalls { get; private set; }

    public int OriginLookupCalls { get; private set; }

    public void Seed(Site site) => _byPublicKey[site.PublicKey] = site;

    public Task<Site?> GetByPublicKeyAsync(string publicKey, CancellationToken cancellationToken)
    {
        LookupCalls++;
        return Task.FromResult(_byPublicKey.GetValueOrDefault(publicKey));
    }

    public Task<Site?> GetByIdAsync(SiteId id, CancellationToken cancellationToken)
    {
        LookupCalls++;
        return Task.FromResult(_byPublicKey.Values.FirstOrDefault(s => s.Id == id));
    }

    public Task<bool> AnyAllowsOriginAsync(string origin, CancellationToken cancellationToken)
    {
        OriginLookupCalls++;
        return Task.FromResult(_byPublicKey.Values.Any(s => s.AllowedOrigins.Contains(origin)));
    }
}
