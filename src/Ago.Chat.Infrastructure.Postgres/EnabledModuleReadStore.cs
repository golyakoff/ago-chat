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

    // `23-14`: no `expires_at` filter at all - the platform owner's detail read needs the whole
    // history, including a lapsed grant, so that "the module vanished" and "the module was never
    // granted" stay distinguishable (this file's own interface remarks). `is_active` is projected
    // rather than filtered on, computed by the identical comparison `Sql`'s own `WHERE` clause above
    // uses to decide inclusion - so a caller reading it is trusting the same live decision the
    // production hot path makes, not a second one.
    private const string AllSql = """
        select module_key as "ModuleKey", trigger_words as "TriggerWords", entry_point as "EntryPoint",
               granted_by_owner as "GrantedByOwner", expires_at as "ExpiresAt",
               (expires_at is null or expires_at > @Now) as "IsActive"
        from enabled_modules
        where site_id = @SiteId
        """;

    public async Task<IReadOnlyList<EnabledModuleSummary>> GetForSiteAsync(
        SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<EnabledModuleRow>(new CommandDefinition(
            Sql, new { SiteId = siteId.Value, Now = now }, cancellationToken: cancellationToken));

        return rows.Select(ToSummary).ToList();
    }

    public async Task<IReadOnlyList<EnabledModuleDetailSummary>> GetAllForSiteAsync(
        SiteId siteId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<EnabledModuleDetailRow>(new CommandDefinition(
            AllSql, new { SiteId = siteId.Value, Now = now }, cancellationToken: cancellationToken));

        return rows.Select(ToDetailSummary).ToList();
    }

    private static EnabledModuleSummary ToSummary(EnabledModuleRow r) => new(
        new ModuleKey(r.ModuleKey),
        JsonSerializer.Deserialize<List<string>>(r.TriggerWords, TriggerWordsOptions)!,
        new Uri(r.EntryPoint, UriKind.Absolute),
        new ModuleCredential(r.Credential),
        r.GrantedByOwner,
        r.ExpiresAt);

    private static EnabledModuleDetailSummary ToDetailSummary(EnabledModuleDetailRow r) => new(
        new ModuleKey(r.ModuleKey),
        JsonSerializer.Deserialize<List<string>>(r.TriggerWords, TriggerWordsOptions)!,
        new Uri(r.EntryPoint, UriKind.Absolute),
        r.GrantedByOwner,
        r.ExpiresAt,
        r.IsActive);

    private sealed class EnabledModuleRow
    {
        public string ModuleKey { get; init; } = string.Empty;

        public string TriggerWords { get; init; } = string.Empty;

        public string EntryPoint { get; init; } = string.Empty;

        public string Credential { get; init; } = string.Empty;

        public bool GrantedByOwner { get; init; }

        public DateTimeOffset? ExpiresAt { get; init; }
    }

    /// <summary>`23-14`: <see cref="EnabledModuleRow"/>'s shape minus <see cref="EnabledModuleRow.Credential"/>
    /// (never selected by <see cref="AllSql"/> - the owner detail read has no use for it, the same
    /// hygiene <see cref="EnabledModuleDetailSummary"/>'s own remarks describe), plus
    /// <see cref="IsActive"/>.</summary>
    private sealed class EnabledModuleDetailRow
    {
        public string ModuleKey { get; init; } = string.Empty;

        public string TriggerWords { get; init; } = string.Empty;

        public string EntryPoint { get; init; } = string.Empty;

        public bool GrantedByOwner { get; init; }

        public DateTimeOffset? ExpiresAt { get; init; }

        public bool IsActive { get; init; }
    }
}
