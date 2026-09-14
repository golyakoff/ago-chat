using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// `25-83`: the soft-threshold email's own once-per-crossing guard - nullable, set exactly once
    /// per `(site_id, period_month)` the first time `Ago.Chat.Worker.DownloadThresholdWatchdogJob`
    /// finds this row's own `bytes_out` at or past its tier's soft threshold, the same
    /// `inactivity_warning_sent_at` shape `Stage23AddSiteActivityWatchdog` already established for
    /// the identical "a scheduled sweep must not re-notify every tick" problem
    /// (`InactivityWatchdogQuery.MarkWarnedAsync`'s own remarks). Reset to `null` implicitly every
    /// month: a fresh `(site_id, period_month)` row is inserted with `bytes_out = 0` by
    /// `AttachmentEgressMeterStore`'s own upsert on that month's first download, and this column
    /// starts `null` right along with it - no explicit monthly reset job needed, unlike
    /// `inactivity_warning_sent_at`'s own explicit clear-on-reset (that flag lives on a single
    /// long-lived `sites` row with no natural period boundary to restart it; this one already has one).
    /// </summary>
    public partial class Stage25AddSiteAttachmentEgressSoftNotifiedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE site_attachment_egress ADD COLUMN soft_notified_at timestamptz NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE site_attachment_egress DROP COLUMN soft_notified_at;");
        }
    }
}
