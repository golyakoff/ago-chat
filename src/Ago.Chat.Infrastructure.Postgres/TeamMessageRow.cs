namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>Dapper's raw row shape for <see cref="TeamMessageReadStore"/> - a top-level type for the
/// identical reason <see cref="MessageRow"/> is one (dynamic-method deserializer, constructor
/// binding). <see cref="CreatedAt"/> stays <see cref="DateTime"/>, not <see cref="DateTimeOffset"/>,
/// for <see cref="MessageRow"/>'s own reason: Npgsql returns <c>timestamptz</c> as a UTC-kinded
/// <see cref="DateTime"/> over raw ADO.NET/Dapper, and <see cref="TeamMessageReadStore"/> converts it
/// before this type crosses back over <see cref="Application.Abstractions.ITeamMessageReadStore"/> -
/// <see cref="DateTime"/> never leaves Infrastructure (date-and-time.md).</summary>
internal sealed record TeamMessageRow(
    Guid Id, int Sequence, Guid AuthorOperatorId, string? AuthorDisplayName, string? AuthorEmail,
    bool AuthorIsAdmin, string Body, DateTime CreatedAt, Guid? ClientMessageId);
