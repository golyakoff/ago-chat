using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `25-67`: <see cref="IVisitorRepository.SaveAsync"/>'s own technology-agnostic signal that the
/// insert it just tried to commit lost a race for the same <see cref="VisitorId"/> - `PK_visitors`
/// tripped, not an optimistic-concurrency token (<see cref="Infrastructure.Postgres.VisitorRepository"/>'s
/// own remarks explain why <see cref="Visitor"/> carries no `xmin` check the way
/// <see cref="ConversationConcurrencyConflictException"/>'s own aggregate does). Declared here, next to
/// the port it belongs to, for the identical reason <see cref="ConversationConcurrencyConflictException"/>
/// is: clean-architecture.md's dependency rule keeps `Ago.Chat.Application` free of any EF Core/Npgsql
/// reference, so the adapter (`Ago.Chat.Infrastructure.Postgres.VisitorRepository`) is the one place
/// that knows the underlying exception is EF's/Npgsql's, and it translates that into this type at the
/// port boundary before it ever reaches a handler. `StartConversationHandler` catches this once and
/// re-reads rather than retrying a write - see its own remarks for why there is nothing to reapply.
/// </summary>
public sealed class VisitorConcurrencyConflictException(VisitorId visitorId)
    : Exception($"Visitor {visitorId.Value} was created concurrently before it could be saved.")
{
    public VisitorId VisitorId { get; } = visitorId;
}
