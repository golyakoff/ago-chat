using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// `23-82`: the maintained per-tenant-per-month egress aggregate - "count downloads and outgoing
    /// bytes per tenant per month... from something maintained (not derived by summing rows on read)"
    /// (the backlog item's own Scope). Written by a plain upsert (<c>AttachmentEgressMeterStore</c>),
    /// the identical bypass-the-aggregate shape <c>sites.attachment_bytes_reserved</c> (`23-76`) already
    /// established - <c>SiteConfiguration</c> registers that column as an EF shadow property purely so
    /// the schema stays in one model; this table gets no EF entity at all, shadow or otherwise, because
    /// nothing here is ever read through <c>AgoChatDbContext</c> - every read goes through Dapper
    /// (<c>AttachmentEgressReadStore</c>), the same "writes through EF, reads through Dapper" split
    /// (adr/0004) every other read store in this codebase already follows, with the one difference that
    /// the write side here is also raw SQL rather than EF's change tracker, for the same reason
    /// <c>SiteAttachmentStorageBudgetStore</c>'s own upsert is: an upsert is not a domain-aggregate
    /// mutation, it is a running total with no invariant a domain type would enforce.
    ///
    /// <para><b><c>period_month</c> is a plain <c>date</c>, always the first of its calendar month</b> -
    /// never a <c>timestamptz</c>, because this column is a bucket key compared for equality
    /// (<c>ON CONFLICT (site_id, period_month)</c>), never a point in time compared with
    /// <c>&lt;</c>/<c>&gt;</c>; rule 11's "UTC DateTimeOffset, always" is about instants, and a
    /// calendar-month bucket is deliberately not one - <c>AttachmentEgressMeterStore</c>'s own caller
    /// computes it from <c>IClock.UtcNow</c>, never from a naive local clock, so the bucket a download
    /// lands in is still UTC-anchored even though the column itself carries no offset.</para>
    ///
    /// <para><b><c>ON DELETE CASCADE</c> to <c>sites</c></b>, the same shape `23-76`'s own
    /// <c>attachment_bytes_reserved</c> column implicitly gets by living on the <c>sites</c> row itself -
    /// a site erasure (`16-02`) must not leave a months-long trail of egress figures for a tenant that
    /// no longer exists, and this table carries no evidentiary purpose (`adr/0111`'s no-FK reasoning is
    /// for a record that must outlive its subject; this is an operational counter, the opposite
    /// case).</para>
    /// </summary>
    public partial class Stage23AddSiteAttachmentEgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE site_attachment_egress (
                    site_id uuid NOT NULL,
                    period_month date NOT NULL,
                    download_count bigint NOT NULL DEFAULT 0,
                    bytes_out bigint NOT NULL DEFAULT 0,
                    CONSTRAINT pk_site_attachment_egress PRIMARY KEY (site_id, period_month),
                    CONSTRAINT fk_site_attachment_egress_sites_site_id FOREIGN KEY (site_id)
                        REFERENCES sites (id) ON DELETE CASCADE
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE site_attachment_egress;");
        }
    }
}
