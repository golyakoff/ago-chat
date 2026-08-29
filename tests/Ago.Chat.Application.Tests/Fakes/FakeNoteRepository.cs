using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.Fakes;

public sealed class FakeNoteRepository : INoteRepository
{
    private readonly List<ConversationNote> _notes = [];

    public IReadOnlyList<ConversationNote> Saved => _notes;

    public Task SaveAsync(ConversationNote note, CancellationToken cancellationToken)
    {
        _notes.Add(note);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationNote>> GetForConversationAsync(
        ConversationId conversationId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ConversationNote>>(
            _notes.Where(n => n.ConversationId == conversationId).OrderBy(n => n.CreatedAt).ToList());
}
