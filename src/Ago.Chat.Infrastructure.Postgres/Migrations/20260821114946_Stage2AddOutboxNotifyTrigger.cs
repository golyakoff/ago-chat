using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Stage2AddOutboxNotifyTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 2-04: lets the dispatcher wake on INSERT instead of waiting out its poll interval
            // (messaging.md's "poll-plus-notify"). A database trigger, not a change to 2-02's already
            // -shipped EfOutboxWriter, per the backlog's own "implementer's choice" - this needs no
            // code change to the writer at all, and the notification carries no payload the dispatcher
            // trusts (it just means "go look"), so a stale or missed notification is harmless by
            // construction (the poll interval remains the fallback, per messaging.md).
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION notify_outbox_insert() RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('outbox_new_row', NEW.id::text);
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER outbox_notify_trigger
                AFTER INSERT ON outbox
                FOR EACH ROW EXECUTE FUNCTION notify_outbox_insert();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS outbox_notify_trigger ON outbox;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS notify_outbox_insert();");
        }
    }
}
