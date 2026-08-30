using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeVisitorContactDetailRepository : IVisitorContactDetailRepository
{
    private readonly Dictionary<VisitorContactDetailId, VisitorContactDetail> _byId = [];

    public IReadOnlyCollection<VisitorContactDetail> All => _byId.Values;

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
