using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakePersonNoteRepository : IPersonNoteRepository
{
    private readonly List<PersonNote> _notes = [];

    public IReadOnlyList<PersonNote> Saved => _notes;

    public Task SaveAsync(PersonNote note, CancellationToken cancellationToken)
    {
        _notes.Add(note);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PersonNote>> GetForPersonAsync(VisitorId personId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PersonNote>>(
            _notes.Where(n => n.PersonId == personId).OrderBy(n => n.CreatedAt).ToList());
}
