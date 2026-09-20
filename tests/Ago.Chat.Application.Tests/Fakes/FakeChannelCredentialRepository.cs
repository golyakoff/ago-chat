using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeChannelCredentialRepository : IChannelCredentialRepository
{
    private readonly Dictionary<ChannelCredentialId, ChannelCredential> _byId = [];

    public Task<ChannelCredential?> GetActiveAsync(SiteId siteId, ChannelKind kind, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.FirstOrDefault(c => c.SiteId == siteId && c.Kind == kind && c.Active));

    public Task<ChannelCredential?> GetByIdAsync(ChannelCredentialId id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<IReadOnlyList<ChannelCredential>> GetAllActiveAsync(ChannelKind kind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ChannelCredential>>(_byId.Values.Where(c => c.Kind == kind && c.Active).ToList());

    // `14-10`: IChannelCredentialRepository.GetActiveByProviderAccountIdAsync's own remarks - the lookup
    // WhatsAppWebhookEndpoints needs since that channel's inbound webhook carries no per-tenant path
    // segment to resolve a credential by id from.
    public Task<ChannelCredential?> GetActiveByProviderAccountIdAsync(
        ChannelKind kind, string providerAccountId, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values.FirstOrDefault(
            c => c.Kind == kind && c.Active && c.ProviderAccountId == providerAccountId));

    public Task SaveAsync(ChannelCredential credential, CancellationToken cancellationToken)
    {
        _byId[credential.Id] = credential;
        return Task.CompletedTask;
    }

    // `25-177`: this fake stores and returns the identical instance reference for a given id (`GetByIdAsync`/
    // `SaveAsync` above) rather than a separate copy the way a real database row is - there is no second
    // "current, stored" value for a reload to ever diverge from, so nothing to do here.
    public Task ReloadAsync(ChannelCredential credential, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Seed(ChannelCredential credential) => _byId[credential.Id] = credential;
}
