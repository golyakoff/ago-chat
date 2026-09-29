using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`adr/0189`/`26-275`: records what the handler asked to flag and answers with whatever outcome
/// the test scripted - the store's own idempotency and cascade are the real adapter's/job's to prove,
/// against Postgres (see <c>PersonErasureIntegrationTests</c>).</summary>
public sealed class FakePersonErasureStore(PersonErasureOutcome outcome = PersonErasureOutcome.Requested) : IPersonErasureStore
{
    public List<(SiteId AccountId, VisitorId PersonId, DateTimeOffset OccurredAt)> Requested { get; } = [];

    public Task<PersonErasureOutcome> RequestErasureIfPresentAsync(
        SiteId accountId, VisitorId personId, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        Requested.Add((accountId, personId, occurredAt));
        return Task.FromResult(outcome);
    }
}
