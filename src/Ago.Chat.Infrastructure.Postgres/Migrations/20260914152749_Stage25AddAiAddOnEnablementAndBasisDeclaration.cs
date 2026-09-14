using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ago.Chat.Infrastructure.Postgres.Migrations
{
    /// <summary>
    /// `25-04`. Two tables and one seeded document.
    ///
    /// <para><b>Why a data seed in a migration at all, when `24-02` publishes documents through the
    /// platform owner's own endpoint.</b> A deployment with no `ai-processing-addendum` row cannot
    /// enable the add-on (`EnableAiAddOnHandler` refuses with `AgreementNotPublished`), which is safe
    /// but would make a fresh environment silently unable to sell the feature until somebody remembered
    /// a manual step. Seeding v1 here makes the first version arrive with the schema that needs it;
    /// every *later* version still goes through `OwnerDocumentEndpoints` and appends, exactly as
    /// `24-02` intended - nothing here is an alternative publishing path.</para>
    ///
    /// <para><b>The text is a draft awaiting legal review, and says so in its own body.</b> `25-04`
    /// decision 4: the agreement is AGO's own words to its own counterparty, so AGO writes it - but the
    /// item is explicit that a lawyer reviews it before it is ever published to a real tenant. The
    /// warning line is inside the seeded body rather than in a comment nobody reading the console would
    /// see.</para>
    /// </summary>
    public partial class Stage25AddAiAddOnEnablementAndBasisDeclaration : Migration
    {
        // Fixed ids: this row is seeded exactly once, by exactly this migration, so deterministic ids
        // make the seed idempotent under a re-run of a hand-repaired environment and make the row
        // quotable in a runbook. Not UUIDv7 - nothing here is time-ordered against anything else.
        private static readonly Guid AgreementDocumentId = new("a9d3f1c2-5b47-4e8a-9c31-6f0d2b7e4a10");
        private static readonly Guid AgreementVersionId = new("b1e7c084-2d36-4f59-8a7b-3c5e91d0f628");

        private const string AgreementDocumentKey = "ai-processing-addendum";

        private const string AgreementTitle = "Обработка переписки с использованием ИИ - дополнительное соглашение";

        /// <summary>`25-04`'s own "Draft of the agreement text", seeded verbatim. Not legal text - the
        /// item's own words - and the first line of the body says so to whoever reads it in the console.</summary>
        private const string AgreementBody = """
            ВНИМАНИЕ: черновик, не прошедший юридическую проверку. Не публиковать реальному арендатору
            до вычитки юристом (`25-04`).

            1. Подключая этот модуль, вы поручаете AGO передавать текст переписки ваших посетителей
               стороннему поставщику ИИ-сервиса для двух целей: подсказки оператору при составлении
               ответа и автоматической категоризации завершённых диалогов.
            2. Передаётся текст сообщений диалога. Не передаются: контактные данные, вложения, сведения
               об операторах и любые данные других арендаторов.
            3. Передача начинается с момента подключения модуля и распространяется только на диалоги,
               созданные после него. Ранее завершённые диалоги не передаются.
            4. Поставщик указан в вашей консоли и может быть заменён с предварительным уведомлением;
               замена поставщика даёт вам право отключить модуль без потери оплаченного периода.
            5. Вы остаётесь оператором персональных данных ваших посетителей. Подключая модуль, вы
               подтверждаете, что у вас есть законное основание для передачи их данных указанному
               поставщику. AGO это основание не проверяет и не заменяет.
            6. Отключить модуль можно в любой момент. С момента отключения передача прекращается; ранее
               переданное у поставщика удаляется на его условиях, которые AGO не контролирует.
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ai_add_on_enablements",
                columns: table => new
                {
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    enabled_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    enabled_by = table.Column<Guid>(type: "uuid", nullable: true),
                    accepted_document_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    accepted_document_version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    disabled_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    disabled_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_add_on_enablements", x => x.site_id);
                    table.ForeignKey(
                        name: "FK_ai_add_on_enablements_sites_site_id",
                        column: x => x.site_id,
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ai_processing_basis_declarations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    declared_by = table.Column<Guid>(type: "uuid", nullable: false),
                    declared_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    client_ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_processing_basis_declarations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_ai_basis_declarations_site",
                table: "ai_processing_basis_declarations",
                columns: new[] { "site_id", "declared_at" });

            // `24-02`'s own shape: a `documents` row carrying the key and the sequence counter, plus one
            // `published_document_versions` row whose `version` is "v{sequence}" - the spelling
            // `Document.Publish` itself derives, restated here because a migration cannot call domain code.
            migrationBuilder.InsertData(
                table: "documents",
                columns: new[] { "id", "document_key", "last_sequence" },
                values: new object[] { AgreementDocumentId, AgreementDocumentKey, 1 });

            migrationBuilder.InsertData(
                table: "published_document_versions",
                columns: new[] { "id", "document_id", "document_key", "sequence", "version", "title", "body", "published_at" },
                values: new object[]
                {
                    AgreementVersionId, AgreementDocumentId, AgreementDocumentKey, 1, "v1",
                    AgreementTitle, AgreementBody,
                    // A fixed instant rather than now(): a migration's own data must be identical in
                    // every environment it runs in, and this is when the text was written, not when a
                    // particular database happened to be upgraded. UTC, rule 11.
                    new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_add_on_enablements");

            migrationBuilder.DropTable(
                name: "ai_processing_basis_declarations");

            // The seeded agreement goes with the tables that needed it. Deleting the version first -
            // `published_document_versions` has a foreign key to `documents`.
            migrationBuilder.DeleteData(
                table: "published_document_versions", keyColumn: "id", keyValue: AgreementVersionId);
            migrationBuilder.DeleteData(
                table: "documents", keyColumn: "id", keyValue: AgreementDocumentId);
        }
    }
}
