using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`24-03`/`24-16`. An in-memory stand-in for <see cref="IRequiredDocumentRepository"/> - empty
/// by default (the same "nothing required today" default the real table starts in), seeded per test via
/// <see cref="Require"/> or through <see cref="AddAsync"/>/<see cref="RemoveAsync"/> themselves, the
/// identical idempotent-set-membership semantics the real
/// <c>Ago.Chat.Infrastructure.Postgres.RequiredDocumentRepository</c> gives.</summary>
public sealed class FakeRequiredDocumentRepository : IRequiredDocumentRepository
{
    private readonly Dictionary<AcceptanceSubjectKind, List<string>> _required = [];

    public void Require(AcceptanceSubjectKind subjectKind, string documentKey)
    {
        if (!_required.TryGetValue(subjectKind, out var keys))
        {
            keys = [];
            _required[subjectKind] = keys;
        }

        keys.Add(documentKey);
    }

    public Task<IReadOnlyList<string>> GetRequiredDocumentKeysAsync(AcceptanceSubjectKind subjectKind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(_required.TryGetValue(subjectKind, out var keys) ? keys : []);

    public Task<bool> AddAsync(AcceptanceSubjectKind subjectKind, string documentKey, CancellationToken cancellationToken)
    {
        if (!_required.TryGetValue(subjectKind, out var keys))
        {
            keys = [];
            _required[subjectKind] = keys;
        }

        if (keys.Contains(documentKey))
        {
            return Task.FromResult(false);
        }

        keys.Add(documentKey);
        return Task.FromResult(true);
    }

    public Task<bool> RemoveAsync(AcceptanceSubjectKind subjectKind, string documentKey, CancellationToken cancellationToken)
    {
        if (!_required.TryGetValue(subjectKind, out var keys))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(keys.Remove(documentKey));
    }
}
