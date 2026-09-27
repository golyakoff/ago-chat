using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `26-230`: defense-in-depth for EF's own write path, the sibling of
/// <see cref="DapperDateTimeOffsetTypeHandler"/> for the other half of this project's Postgres access.
/// Npgsql's EF Core provider throws the identical <c>ArgumentException: Cannot write DateTimeOffset with
/// Offset=... to PostgreSQL type 'timestamp with time zone', only offset 0 (UTC) is supported</c> a
/// Dapper parameter carrying a non-UTC offset does - the exception is Npgsql's own writer, not
/// specific to either access path.
///
/// <para><b>Not exercised by the live bug, and not required to fix it.</b> Every <see cref="DateTimeOffset"/>
/// this project writes through EF today comes from <c>IClock.UtcNow</c> (`date-and-time.md`'s own rule),
/// never from a caller-supplied value the way the Dapper report/search read stores' own `from`/`to`
/// windows do - so nothing currently trips this. That is caller discipline holding so far, not a contract
/// the type system enforces, which is exactly the gap `26-230`'s own root cause already proved does not
/// hold once a real caller (the Android client) supplies a non-UTC offset somewhere Npgsql sees it. This
/// converter closes the identical gap on the write path nothing has hit yet, rather than waiting for it
/// to.</para>
///
/// <para><b>Same instant, only the wire offset changes</b> - the identical guarantee
/// <see cref="DapperDateTimeOffsetTypeHandler"/>'s own remarks give: <see cref="DateTimeOffset.ToUniversalTime"/>
/// re-expresses the same point on the timeline with <c>Offset</c> zero, it does not shift it. Reading a
/// value back needs no conversion at all - Npgsql already returns a UTC-offset <see cref="DateTimeOffset"/>
/// for `timestamptz`, so the provider-to-model direction is the identity function.</para>
///
/// <para><b>Applied model-wide, once, via <c>ConfigureConventions</c></b> (<see cref="AgoChatDbContext"/>) -
/// not repeated on every <c>DateTimeOffset</c> property across every entity configuration. EF Core applies
/// a conversion registered this way to both <see cref="DateTimeOffset"/> and <see cref="Nullable{T}"/> of
/// it automatically, so a new column added later inherits the guard without anyone remembering to ask for
/// it - the same "one seam, not N call sites" reasoning <see cref="DapperDateTimeOffsetTypeHandler"/>'s own
/// remarks give for the read side.</para>
/// </summary>
internal sealed class DateTimeOffsetUtcConverter() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    v => v.ToUniversalTime(),
    v => v)
{
}
