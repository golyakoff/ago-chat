namespace Ago.Chat.Application.UseCases.GetPersonNotes;

public sealed record PersonNoteDto(Guid Id, Guid AuthorId, string Body, DateTimeOffset CreatedAt);
