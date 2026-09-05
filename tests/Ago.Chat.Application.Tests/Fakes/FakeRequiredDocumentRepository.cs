using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

/// <summary>`24-03`. An in-memory stand-in for <see cref="IRequiredDocumentRepository"/> - empty by
/// default (the same "nothing required today" default the real table starts in), seeded per test via
/// <see cref="Require"/>.</summary>
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
}
