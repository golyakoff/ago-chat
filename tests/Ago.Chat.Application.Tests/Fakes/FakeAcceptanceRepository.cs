using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`24-01`. No delete method - <see cref="IAcceptanceRepository"/>'s own remarks; this fake
/// has nowhere to add one even if a test wanted to.</summary>
public sealed class FakeAcceptanceRepository : IAcceptanceRepository
{
    private readonly List<AcceptanceRecord> _records = [];

    public IReadOnlyList<AcceptanceRecord> Saved => _records;

    public Task SaveAsync(AcceptanceRecord record, CancellationToken cancellationToken)
    {
        _records.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AcceptanceRecord>> GetForSubjectAsync(
        AcceptanceSubjectKind subjectKind, Guid subjectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AcceptanceRecord>>(
            _records.Where(r => r.SubjectKind == subjectKind && r.SubjectId == subjectId).OrderBy(r => r.AcceptedAt).ToList());
}
