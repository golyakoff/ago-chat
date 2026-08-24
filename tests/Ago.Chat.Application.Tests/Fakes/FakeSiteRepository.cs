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

    // `11-01`: no real persistence semantics to fake here (no EF change tracker, no Detached-state
    // branch) - Seed already indexes by PublicKey, so a site mutated in place by
    // UpdateWidgetConfigHandler (loaded via GetByIdAsync, the same in-memory instance) is already
    // "saved" from this fake's point of view; this just re-indexes it defensively in case a future
    // test builds a Site and calls SaveAsync without ever seeding it first.
    public Task SaveAsync(Site site, CancellationToken cancellationToken)
    {
        _byPublicKey[site.PublicKey] = site;
        return Task.CompletedTask;
    }
}
