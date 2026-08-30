using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>In-memory <see cref="IPendingChannelLinkRequestRepository"/> - a plain dictionary keyed by
/// id, mirroring <see cref="FakeChannelIdentityRepository"/>'s own shape. <see cref="Stage"/> and
/// <see cref="SaveAsync"/> both simply upsert here - this fake has no separate "commit" concept to model,
/// since a test asserts on <see cref="All"/> regardless of which path wrote a row.</summary>
public sealed class FakePendingChannelLinkRequestRepository : IPendingChannelLinkRequestRepository
{
    private readonly Dictionary<PendingChannelLinkRequestId, PendingChannelLinkRequest> _byId = [];

    public IReadOnlyCollection<PendingChannelLinkRequest> All => _byId.Values;

    public Task<PendingChannelLinkRequest?> FindLiveAsync(
        SiteId siteId, ChannelKind kind, byte[] codeHash, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.Values
            .Where(p => p.SiteId == siteId && p.Kind == kind && p.CodeHash.AsSpan().SequenceEqual(codeHash)
                && p.ConsumedAt is null && p.ExpiresAt > now)
            .OrderByDescending(p => p.CreatedAt)
            .FirstOrDefault());

    public Task SaveAsync(PendingChannelLinkRequest request, CancellationToken cancellationToken)
    {
        Stage(request);
        return Task.CompletedTask;
    }

    public void Stage(PendingChannelLinkRequest request)
    {
        _byId[request.Id] = request;
    }
}
