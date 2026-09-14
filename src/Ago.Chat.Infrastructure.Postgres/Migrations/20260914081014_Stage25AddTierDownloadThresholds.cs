using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// `25-83`: the two per-tariff-tier thresholds `docs/backlog/25-83-*.md`'s own Done-when demands
    /// - "never a single universal constant." The identical "no EF entity at all, every read through
    /// Dapper" shape `Stage23AddSiteAttachmentEgress`'s own remarks establish for
    /// `site_attachment_egress`, for the identical reason: this table is read by
    /// `IDownloadThresholdReadStore` alone, never through `AgoChatDbContext`, and its two rows are
    /// written by hand (a runbook script, `docs/runbooks/`) rather than through any domain aggregate -
    /// there is no `Site`-shaped invariant here, only two per-tier numbers the platform owner edits
    /// directly.
    ///
    /// <para><b>Keyed by <c>tier</c> (a plain <c>text</c>, not a foreign key)</b> - the same
    /// "tier is a string this codebase names in code, never a row in its own lookup table"
    /// convention `SubscriptionTierBands`'s own constants already establish (`Site.Tier` itself is a
    /// plain string column with no foreign key either). A tier with no row here is read by
    /// `IDownloadThresholdReadStore` as "never blocked" rather than refused outright - see that
    /// store's own remarks for why a missing row fails open, not closed.</para>
    ///
    /// <para><b>The seeded numbers are a labelled starting point, not a measured figure</b> - the
    /// identical "starting point, not measured" posture `AttachmentStorageQuotaOptions`'s own remarks
    /// already state for its own free/paid byte ceilings (CLAUDE.md: "do not invent numbers... measure
    /// or stay silent"). Free/Solo: 500 MiB soft, 750 MiB hard - roughly the same order of magnitude as
    /// `AttachmentStorageQuotaOptions.FreeTierTotalBytes`'s own 100 MiB total storage ceiling, scaled up
    /// because downloads of the same file recur monthly while storage does not. Business/starter:
    /// 8 GiB soft, 10 GiB hard - eight times `PaidTierBytesPerPaidYear`'s own 1 GiB, the same "one
    /// order of magnitude above the free tier, round number, owner-adjustable" reasoning. Both rows are
    /// ordinary data, editable by hand after this migration - there is no code path anywhere that
    /// re-derives them.</para>
    /// </summary>
    public partial class Stage25AddTierDownloadThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE tier_download_thresholds (
                    tier text NOT NULL,
                    soft_threshold_bytes bigint NOT NULL,
                    hard_threshold_bytes bigint NOT NULL,
                    updated_at timestamptz NOT NULL,
                    updated_by text NOT NULL,
                    CONSTRAINT pk_tier_download_thresholds PRIMARY KEY (tier),
                    CONSTRAINT ck_tier_download_thresholds_positive CHECK (soft_threshold_bytes > 0),
                    CONSTRAINT ck_tier_download_thresholds_hard_above_soft
                        CHECK (hard_threshold_bytes > soft_threshold_bytes)
                );

                INSERT INTO tier_download_thresholds (tier, soft_threshold_bytes, hard_threshold_bytes, updated_at, updated_by)
                VALUES
                    ('free', 524288000, 786432000, now(), 'migration:Stage25AddTierDownloadThresholds'),
                    ('starter', 8589934592, 10737418240, now(), 'migration:Stage25AddTierDownloadThresholds');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE tier_download_thresholds;");
        }
    }
}
