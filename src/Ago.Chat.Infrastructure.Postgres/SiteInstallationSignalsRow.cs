namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// Dapper's raw row shape for <see cref="SiteInstallationSignalRepository.GetAsync"/> - a top-level
/// type so Dapper's constructor-matching binds by name, the same reason <c>MessageRow</c>/
/// <c>ConversationSummaryRow</c> are top-level types rather than inline tuples (each file's own
/// remarks).
///
/// <para><see cref="DateTime"/>, not <see cref="DateTimeOffset"/> - Npgsql/Dapper over raw ADO.NET
/// returns `timestamp with time zone` as a UTC-kinded <see cref="DateTime"/>, the identical reason
/// <c>MessageRow</c>'s own doc comment gives; <see cref="SiteInstallationSignalRepository.GetAsync"/>
/// is what converts each one to a labelled <see cref="DateTimeOffset"/> for the rest of the
/// codebase.</para>
/// </summary>
public sealed record SiteInstallationSignalsRow(
    DateTime? FirstSeenAt, DateTime? LastSeenAt, string? LastRefusedOrigin, DateTime? LastRefusedOriginAt);
