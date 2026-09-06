using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `23-07`: migration-scaffolding only, the same role <see cref="ErasureRecordEntity"/> and
/// <see cref="MessageArchiveEntity"/> already play for their own tables - see either type's own
/// remarks for why a row with no aggregate behind it is still declared as a plain EF entity
/// (`dotnet ef migrations add` then applies this project's own naming/conversion conventions rather
/// than a second, hand-maintained source of truth for them). Nothing queries this DbSet through EF:
/// <see cref="WidgetActivityWriter"/> (the flush) and <see cref="WidgetActivityReadStore"/> (the read)
/// are both raw Npgsql/Dapper, for the identical reason those two files' own remarks give.
///
/// <para>One row per (site, UTC calendar day) - <see cref="Day"/> is a <see cref="DateOnly"/>, the
/// same type <see cref="MessageArchiveEntity.PeriodStart"/> already uses for a bucket that means "this
/// calendar day", not an instant. The three counts are deltas the flusher adds to, never overwritten -
/// see <see cref="WidgetActivityWriter"/>'s own `ON CONFLICT ... DO UPDATE` for the upsert this shape
/// exists to make possible.</para>
/// </summary>
internal sealed class SiteWidgetActivityEntity
{
    public SiteId SiteId { get; set; }

    public DateOnly Day { get; set; }

    public int Loads { get; set; }

    public int Opens { get; set; }

    public int Conversations { get; set; }
}
