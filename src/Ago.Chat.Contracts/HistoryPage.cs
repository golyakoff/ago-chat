namespace Ago.Chat.Contracts;

public sealed record HistoryPage(IReadOnlyList<MessageDto> Messages, int? NextBeforeSequence);
