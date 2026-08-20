namespace Ago.Chat.Domain;

/// <summary>
/// An in-memory fact something in <see cref="Ago.Chat.Domain"/> raised. Not a wire contract - that is
/// <c>Ago.Chat.Contracts</c>'s job, mapped deliberately, never serialized directly
/// (clean-architecture.md). Nothing publishes these yet; the outbox arrives in Stage 2.
/// </summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
