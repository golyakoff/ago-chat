namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// Dapper's raw row shape for <see cref="ConversationReadStore.GetLatestMessagesAsync"/> - a
/// top-level type for the same reason <see cref="MessageRow"/> is one (Dapper's dynamic-method
/// deserializer needs a public constructor it can bind to without nested-type visibility questions).
/// <see cref="CreatedAt"/> is <see cref="DateTime"/>, not <see cref="DateTimeOffset"/>, for the exact
/// reason <see cref="MessageRow"/>'s own doc comment gives (Npgsql over raw Dapper, not EF) -
/// <see cref="ConversationReadStore"/> converts it before this type crosses back over
/// <c>IConversationReadStore</c> as a <see cref="Application.Abstractions.LatestMessageSummary"/>.
/// </summary>
internal sealed record LatestMessageRow(
    Guid ConversationId, string Body, DateTime CreatedAt, string? ContentKind = null, Guid? AttachmentId = null);
