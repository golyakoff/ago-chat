namespace Ago.Chat.Contracts;

/// <summary>The realtime protocol's wire shape for one message (api-design.md: "payload shapes live
/// in Ago.Chat.Contracts and are versioned with the same additive-only rule as integration
/// events").</summary>
public sealed record MessageDto(
    Guid Id, int Sequence, string AuthorKind, Guid AuthorId, string Body, DateTimeOffset CreatedAt);
