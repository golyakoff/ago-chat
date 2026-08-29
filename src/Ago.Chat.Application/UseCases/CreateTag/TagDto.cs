namespace Ago.Chat.Application.UseCases.CreateTag;

public sealed record TagDto(Guid Id, string Name, DateTimeOffset CreatedAt);
