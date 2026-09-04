using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`20-07`: the Dapper adapter for <see cref="IEnabledModuleReadStore"/> - the same
/// "hand-written SQL over the write model, never through the aggregate" shape
/// <see cref="ConversationReadStore"/> already establishes (adr/0004).</summary>
public sealed class EnabledModuleReadStore(NpgsqlDataSource dataSource) : IEnabledModuleReadStore
{
    private static readonly JsonSerializerOptions TriggerWordsOptions = new(JsonSerializerDefaults.Web);

    // `22-17`: `expires_at is null or expires_at > @Now` - an expired grant simply is not in this
    // result set, which every caller (registration-time conflict check, per-message trigger match,
    // the console listing) already treats "not enabled" as meaning. `@Now` is a parameter this store
    // is handed, never `now()`: this codebase compares instants sourced from `IClock`
    // (`CLAUDE.md` rule 11), not the database server's own clock - see this interface's own remarks.
    private const string Sql = """
        select module_key as "ModuleKey", trigger_words as "TriggerWords", entry_point as "EntryPoint",
               credential as "Credential", granted_by_owner as "GrantedByOwner", expires_at as "ExpiresAt"
        from enabled_modules
        where site_id = @SiteId and (expires_at is null or expires_at > @Now)
        """;

    public async Task<IReadOnlyList<EnabledModuleSummary>> GetForSiteAsync(
        SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<EnabledModuleRow>(new CommandDefinition(
            Sql, new { SiteId = siteId.Value, Now = now }, cancellationToken: cancellationToken));

        return rows.Select(ToSummary).ToList();
    }

    private static EnabledModuleSummary ToSummary(EnabledModuleRow r) => new(
        new ModuleKey(r.ModuleKey),
        JsonSerializer.Deserialize<List<string>>(r.TriggerWords, TriggerWordsOptions)!,
        new Uri(r.EntryPoint, UriKind.Absolute),
        new ModuleCredential(r.Credential),
        r.GrantedByOwner,
        r.ExpiresAt);

    private sealed class EnabledModuleRow
    {
        public string ModuleKey { get; init; } = string.Empty;

        public string TriggerWords { get; init; } = string.Empty;

        public string EntryPoint { get; init; } = string.Empty;

        public string Credential { get; init; } = string.Empty;

        public bool GrantedByOwner { get; init; }

        public DateTimeOffset? ExpiresAt { get; init; }
    }
}
