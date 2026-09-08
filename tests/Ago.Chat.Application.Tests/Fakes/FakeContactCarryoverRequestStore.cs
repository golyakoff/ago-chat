using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>Records every call, without any of the real store's upsert/transaction behaviour - that
/// guarantee is proven against real Postgres in `Ago.Chat.Integration.Tests` (testing.md: never mock
/// the database for a guarantee the schema itself provides).</summary>
public sealed class FakeContactCarryoverRequestStore : IContactCarryoverRequestStore
{
    private readonly List<(SiteId SiteId, DateTimeOffset Now)> _requested = [];

    public IReadOnlyList<(SiteId SiteId, DateTimeOffset Now)> Requested => _requested;

    public Task RequestAsync(SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _requested.Add((siteId, now));
        return Task.CompletedTask;
    }
}
