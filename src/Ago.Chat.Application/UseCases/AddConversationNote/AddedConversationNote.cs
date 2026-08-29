namespace Ago.Chat.Application.UseCases.AddConversationNote;

public sealed record AddedConversationNote(Guid Id, Guid ConversationId, Guid AuthorId, string Body, DateTimeOffset CreatedAt);
