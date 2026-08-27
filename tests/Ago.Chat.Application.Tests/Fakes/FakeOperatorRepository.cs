using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeOperatorRepository : IOperatorRepository
{
    private readonly List<Operator> _all = [];

    /// <summary>`13-07`: mirrors <c>OperatorRepository.GetByExternalSubjectIdAndSiteIdAsync</c>
    /// exactly - both columns, never a fallback to a different site for the same identity.</summary>
    public Task<Operator?> GetByExternalSubjectIdAndSiteIdAsync(string externalSubjectId, SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult(_all.Find(o => o.ExternalSubjectId == externalSubjectId && o.SiteId == siteId));

    /// <summary>`13-07`: every seeded row for this identity - before this item, tests could only ever
    /// seed zero or one; this is what lets a test express "more than one tenancy" at all.</summary>
    public Task<IReadOnlyList<Operator>> ListByExternalSubjectIdAsync(string externalSubjectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Operator>>(_all.FindAll(o => o.ExternalSubjectId == externalSubjectId));

    /// <summary>`14-04`: mirrors <c>OperatorRepository</c>'s own predicate exactly - <c>Online</c>
    /// only, no capacity term. A fake that quietly used a different rule than the adapter would let a
    /// test pass against a condition production does not implement.</summary>
    public Task<bool> AnyOnlineForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
        Task.FromResult(_all.Exists(o => o.SiteId == siteId && o.Status == OperatorStatus.Online));

    /// <summary>`4-06`: mirrors <c>OperatorRepository.GetByIdAsync</c> - returns the same seeded
    /// reference, so a caller's <c>GoOnline</c>/<c>GoOffline</c> mutation is visible to every other
    /// method here without <see cref="SaveAsync"/> needing to do anything, exactly as EF's change
    /// tracking makes the real adapter's save implicit for an entity loaded the same way.</summary>
    public Task<Operator?> GetByIdAsync(OperatorId id, CancellationToken cancellationToken) =>
        Task.FromResult(_all.Find(o => o.Id == id));

    public Task SaveAsync(Operator operatorEntity, CancellationToken cancellationToken) => Task.CompletedTask;

    public void Seed(Operator @operator) => _all.Add(@operator);
}
