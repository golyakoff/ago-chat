using System.Data;
using Dapper;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `26-230`: Npgsql 10 (.NET 10) throws <c>ArgumentException: Cannot write DateTimeOffset with
/// Offset=... to PostgreSQL type 'timestamp with time zone', only offset 0 (UTC) is supported</c> the
/// moment a parameter carries a <see cref="DateTimeOffset"/> whose own <see cref="DateTimeOffset.Offset"/>
/// is non-zero — which every report/analytics read store's own <c>from</c>/<c>to</c> window is, the
/// instant a caller (the Android client, `+03:00`) supplies one instead of a UTC `Z` instant. The value
/// is a perfectly good instant; only its own wire *representation* of that instant carries the offset
/// Npgsql refuses to accept for `timestamptz`.
///
/// <para><b>One shared handler, not N call-site conversions.</b> The obvious per-call fix —
/// <c>From = from.ToUniversalTime()</c> repeated at every <c>new { ... }</c> parameter object — was
/// rejected: it requires every future report read store to remember it, which is exactly the kind of
/// caller discipline this very bug proves does not hold (nothing enforced it here either, until a real
/// client sent a real non-UTC offset). Dapper's own <see cref="SqlMapper.AddTypeHandler{T}"/> seam —
/// already used once, by <see cref="DapperDateOnlyTypeHandler"/>, for an unrelated gap — intercepts
/// <em>every</em> <see cref="DateTimeOffset"/> parameter this project ever builds through Dapper, at the
/// one place they all pass through, so a new read store cannot reintroduce this bug by simply forgetting
/// a conversion at its own bind site.</para>
///
/// <para><b>Same instant, only the wire offset changes.</b> <see cref="DateTimeOffset.ToUniversalTime"/>
/// does not shift the range a caller asked for — <c>2026-09-27T00:00:00+03:00</c> and
/// <c>2026-09-26T21:00:00Z</c> are the same point on the timeline, and Npgsql accepts the second
/// representation as freely as it refuses the first. The SQL's own day/zone logic is untouched; this
/// handler only normalizes what crosses the wire.</para>
///
/// <para><b>Registration follows <see cref="DapperDateOnlyTypeHandler"/>'s own settled answer, restated
/// here rather than centralised</b> — that class's own remarks record two rejected registration points:
/// a <c>[ModuleInitializer]</c>, refused by <c>CA2255</c> (a library silently mutating process-global
/// state on load), and <c>AddPostgresPersistence</c>, refuted by the integration tests that construct a
/// read store directly against a data source and never build a container, so that registration would
/// never run. The answer that survived both is the static constructor of every store that needs it — it
/// runs before that store's first query, in a host and in a bare integration test alike, and it is
/// idempotent: both <see cref="SqlMapper.RemoveTypeMap"/> and <see cref="SqlMapper.AddTypeHandler{T}"/>
/// simply repeat the same removal/replacement on a second call rather than complaining, so every
/// affected store calling <see cref="Register"/> from its own static constructor costs nothing.</para>
///
/// <para><b>Not simply <see cref="SqlMapper.AddTypeHandler{T}"/>, unlike the <see cref="DateOnly"/>
/// case.</b> See <see cref="Register"/>'s own remarks: <see cref="DateTimeOffset"/> is already in
/// Dapper's built-in type map, so the handler alone is silently never reached without first removing
/// that built-in entry.</para>
/// </summary>
internal sealed class DapperDateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    internal static readonly DapperDateTimeOffsetTypeHandler Instance = new();

    /// <summary>
    /// The one call every affected store's static constructor makes - and why it is two statements, not
    /// one. <see cref="SqlMapper.LookupDbType"/> consults its own built-in <c>typeMap</c> *before* it ever
    /// looks at a registered <see cref="SqlMapper.ITypeHandler"/>, and <see cref="DateTimeOffset"/>, unlike
    /// <see cref="DateOnly"/> (<see cref="DapperDateOnlyTypeHandler"/>'s own reason for existing), is
    /// already in that built-in map - so <see cref="SqlMapper.AddTypeHandler{T}"/> alone is silently never
    /// consulted for it. Proven empirically here, not merely reasoned about: the fails-before run in this
    /// item's own test (<c>OperatorLoadReportReadStoreTests</c>) still threw the identical
    /// <c>ArgumentException</c> with only <c>AddTypeHandler</c> in place. <see cref="SqlMapper.RemoveTypeMap"/>
    /// is Dapper's own documented escape hatch for exactly this - it deletes the built-in entry so the
    /// lookup falls through to the handler this class provides.
    /// </summary>
    internal static void Register()
    {
        SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
        SqlMapper.AddTypeHandler(Instance);
    }

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
    {
        parameter.DbType = DbType.DateTimeOffset;
        parameter.Value = value.ToUniversalTime();
    }

    /// <summary>
    /// Npgsql already reads a `timestamptz` column back as a UTC-offset <see cref="DateTimeOffset"/>
    /// without this handler's help — this is reached only if a provider ever hands back something else,
    /// the same "defensive fallback, not the normal path" shape <see cref="DapperDateOnlyTypeHandler.Parse"/>
    /// documents for its own type.
    /// </summary>
    public override DateTimeOffset Parse(object value) => value switch
    {
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
        _ => throw new InvalidCastException(
            $"Cannot read a DateTimeOffset from {value?.GetType().FullName ?? "null"}."),
    };
}
