namespace Ago.Chat.Contracts;

/// <summary>`23-32`: the team-chat sibling of <see cref="HistoryPage"/> - a keyset page, newest
/// first, <see cref="NextBeforeSequence"/> <see langword="null"/> once the caller has reached the
/// start of the room's history.</summary>
public sealed record TeamHistoryPage(IReadOnlyList<TeamMessageDto> Messages, int? NextBeforeSequence);
