namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// Dapper's raw row shape for <see cref="ConversationReadStore"/> - a top-level type so Dapper's
/// dynamic-method deserializer can bind to its constructor without nested-type visibility questions.
/// <see cref="CreatedAt"/> is <see cref="DateTime"/>, not <see cref="DateTimeOffset"/>, because
/// Npgsql returns <c>timestamptz</c> as a UTC-kinded <see cref="DateTime"/> over raw ADO.NET/Dapper
/// (EF's own provider does the <see cref="DateTimeOffset"/> conversion itself; this path bypasses
/// EF entirely) - found by running the integration tests: Dapper's constructor-matching needs an
/// exact type match, and reported "System.DateTime CreatedAt" as the signature it needed.
/// <see cref="ConversationReadStore"/> converts it to <see cref="DateTimeOffset"/> before this type
/// crosses back over <c>IConversationReadStore</c> - `DateTime` never leaves Infrastructure
/// (date-and-time.md, the arch test `DateTimeType_NeverAppearsOutsideInfrastructure`).
/// </summary>
internal sealed record MessageRow(
    Guid Id, int Sequence, string AuthorKind, Guid AuthorId, string Body, DateTime CreatedAt, Guid? AttachmentId);
