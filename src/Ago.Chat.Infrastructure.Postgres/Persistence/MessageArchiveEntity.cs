using Ago.Chat.Domain;

namespace Ago.Chat.Infrastructure.Postgres.Persistence;

/// <summary>
/// `13-06`: migration-scaffolding only, the same role <see cref="ExportRequestEntity"/> plays for
/// `export_requests` - see that type's own remarks for why a manifest row with no aggregate behind it
/// is declared as a plain EF entity anyway (`dotnet ef migrations add` then applies this project's own
/// naming/conversion conventions rather than a second, hand-maintained source of truth for them), and
/// why nothing queries it through EF regardless (<see cref="MessageArchiveRepository"/> is raw Npgsql
/// end to end).
/// </summary>
internal sealed class MessageArchiveEntity
{
    public Guid Id { get; set; }

    public SiteId SiteId { get; set; }

    public string RetentionClass { get; set; } = string.Empty;

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    public string ObjectKey { get; set; } = string.Empty;

    public DateTimeOffset ArchivedAt { get; set; }
}
