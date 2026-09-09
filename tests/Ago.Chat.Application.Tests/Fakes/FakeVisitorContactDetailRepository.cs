using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeVisitorContactDetailRepository : IVisitorContactDetailRepository
{
    private readonly Dictionary<VisitorContactDetailId, VisitorContactDetail> _byId = [];

    public IReadOnlyCollection<VisitorContactDetail> All => _byId.Values;

    /// <summary>`25-38`/`25-39`: the same synchronous seed shape <see cref="FakeSiteRepository.Seed"/>
    /// already uses - a test arranging fixture state has no async context of its own to await
    /// <see cref="SaveAsync"/> from, and this fake's own write is synchronous in every way that
    /// matters (an in-memory dictionary, never a real I/O call).</summary>
    public void Seed(VisitorContactDetail detail) => _byId[detail.Id] = detail;

    public Task SaveAsync(VisitorContactDetail detail, CancellationToken cancellationToken)
    {
        _byId[detail.Id] = detail;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VisitorContactDetail>> GetForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VisitorContactDetail>>(
            _byId.Values.Where(d => d.VisitorId == visitorId).OrderBy(d => d.RecordedAt).ToList());

    public Task<VisitorContactDetail?> GetByIdAsync(VisitorContactDetailId id, CancellationToken cancellationToken) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task DeleteAsync(VisitorContactDetail detail, CancellationToken cancellationToken)
    {
        _byId.Remove(detail.Id);
        return Task.CompletedTask;
    }
}
