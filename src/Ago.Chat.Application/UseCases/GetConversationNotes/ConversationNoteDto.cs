namespace Ago.Chat.Application.UseCases.GetConversationNotes;

public sealed record ConversationNoteDto(Guid Id, Guid AuthorId, string Body, DateTimeOffset CreatedAt);
