using System.Data;
using Dapper;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-07`: the first Dapper type handler in this codebase, and it exists because Dapper 2.1.79 has
/// no <see cref="DbType"/> for <see cref="DateOnly"/> at all — <c>SqlMapper.LookupDbType</c> throws
/// <c>NotSupportedException: The member ... of type System.DateOnly cannot be used as a parameter
/// value</c> the moment one is passed as a query parameter.
///
/// <para><b>Npgsql is not the problem and this handler does not work around it.</b> Npgsql has mapped
/// <see cref="DateOnly"/> to <c>date</c> since 6.0, which is why the <c>day date not null</c> column
/// reads back into a <see cref="DateOnly"/> without any help. Only Dapper's own parameter lookup is
/// missing, so this handler does the minimum: it names the type (<see cref="DbType.Date"/>) and hands
/// the value through unchanged.</para>
///
/// <para><b>Deliberately not converted to <c>DateTime</c>.</b> The obvious fix is
/// <c>value.ToDateTime(TimeOnly.MinValue)</c>, and it would work — this is Infrastructure, where
/// `date-and-time.md` permits <c>DateTime</c>. It is refused anyway: turning a calendar day into an
/// instant to satisfy a library invents a time of day, and the next reader has to work out whether
/// that midnight means anything. A <c>date</c> column compared against a date is the honest query.</para>
///
/// <para><b>Where this registers took two wrong answers before the right one, and both were refused by
/// something rather than by taste.</b>
/// <list type="number">
/// <item><description><c>ServiceCollectionExtensions.AddPostgresPersistence</c>, on the reasoning that
/// every instruction this project gives Dapper belongs beside the rest of the Postgres wiring. The
/// integration tests refuted it in one run: they construct <see cref="WidgetActivityReadStore"/>
/// directly against a data source and never build a container, so the registration never ran.</description></item>
/// <item><description>A <c>[ModuleInitializer]</c>, so the assembly's own loading would do it. <c>CA2255</c>
/// refused that, and correctly — a library silently mutating process-global state the moment it loads
/// is the thing that rule exists to stop.</description></item>
/// <item><description>The static constructor of the one store that needs it. It runs before that
/// store's first query, in a host and in a bare test alike, and it mutates nothing until somebody
/// actually uses the type. Idempotent: Dapper replaces a handler for the same type rather than
/// complaining, so a second read store adding the same line later costs nothing.</description></item>
/// </list></para>
/// </summary>
internal sealed class DapperDateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
{
    internal static readonly DapperDateOnlyTypeHandler Instance = new();

    public override void SetValue(IDbDataParameter parameter, DateOnly value)
    {
        parameter.DbType = DbType.Date;
        parameter.Value = value;
    }

    /// <summary>
    /// Reached only if a provider ever hands back something other than a <see cref="DateOnly"/> for a
    /// <c>date</c> column. Npgsql does not, so the <see cref="DateTime"/> arm is a fallback rather than
    /// the normal path — and it is the one place in this file where a conversion is the right answer,
    /// because the value has already been produced by somebody else.
    /// </summary>
    public override DateOnly Parse(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        string s => DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new InvalidCastException(
            $"Cannot read a DateOnly from {value?.GetType().FullName ?? "null"}."),
    };
}
