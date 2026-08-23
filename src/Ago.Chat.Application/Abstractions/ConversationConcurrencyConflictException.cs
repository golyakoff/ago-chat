using Ago.Chat.Domain;

namespace Ago.Chat.Application.Abstractions;

/// <summary>
/// `6-08`: <see cref="IConversationRepository.SaveAsync"/>'s own technology-agnostic signal that the
/// row it just tried to persist had already changed underneath it - the optimistic-concurrency check
/// (`xmin`, `data-model.md`) tripped. Declared here, next to the port it belongs to, rather than
/// letting the handler catch `Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException` directly:
/// clean-architecture.md's dependency rule keeps `Ago.Chat.Application` free of any EF Core reference
/// ("Not EF Core, Npgsql, ..."), so the adapter (`Ago.Chat.Infrastructure.Postgres.ConversationRepository`)
/// is the one place that knows the underlying exception is EF's, and it translates that into this type
/// at the port boundary before it ever reaches a handler - the same shape `ErrorExtensions.ToProblem`
/// already uses at the *outbound* HTTP boundary, mirrored here at the *inbound* persistence boundary.
/// A handler that wants to retry once against fresh state (`CloseConversationHandler`,
/// `AssignConversationHandler`) catches this, never the EF type.
/// </summary>
public sealed class ConversationConcurrencyConflictException(ConversationId conversationId)
    : Exception($"Conversation {conversationId.Value} was modified concurrently before it could be saved.")
{
    public ConversationId ConversationId { get; } = conversationId;
}
