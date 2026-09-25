namespace Ago.Chat.Application.UseCases.AddPersonNote;

public sealed record AddedPersonNote(Guid Id, Guid PersonId, Guid AuthorId, string Body, DateTimeOffset CreatedAt);
