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
    Guid Id, int Sequence, string AuthorKind, Guid AuthorId, string Body, DateTime CreatedAt, Guid? AttachmentId,
    Guid? ClientMessageId,
    // `14-06`: the three structured columns, as the strings the row holds. Raw all the way through -
    // the read model never parses ContentKind or Payload, because parsing the payload here would be
    // AGO Chat looking inside a document it is forbidden to understand, on the hottest read in the
    // product. Deserialising happens once, at the wire boundary (MessageDtoMapper), and only for
    // Actions, whose schema AGO Chat does own.
    string? ContentKind = null, string? Payload = null, string? Actions = null);
