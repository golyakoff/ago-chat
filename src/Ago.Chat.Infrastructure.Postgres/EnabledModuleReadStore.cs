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
    // `22-30`: `and revoked_at is null` - the identical treatment for a revoked grant, which no
    // longer deletes the row (EnabledModule.RevokedAt's own remarks), so this filter is what keeps
    // this method's own external answer unchanged from before that item: a revoked module still
    // reads as "not enabled" here, on the live routing path this comment already describes.
    private const string Sql = """
        select module_key as "ModuleKey", trigger_words as "TriggerWords", entry_point as "EntryPoint",
               credential as "Credential", granted_by_owner as "GrantedByOwner", expires_at as "ExpiresAt"
        from enabled_modules
        where site_id = @SiteId and (expires_at is null or expires_at > @Now) and revoked_at is null
        """;

    // `23-14`: no `expires_at` filter at all - the platform owner's detail read needs the whole
    // history, including a lapsed grant, so that "the module vanished" and "the module was never
    // granted" stay distinguishable (this file's own interface remarks). `22-30`: no `revoked_at`
    // filter either, for the identical reason - and the same reason `Ago.Chat.Worker.SiteErasureJob`
    // reads through this very method to learn every module a site has ever had, revoked or lapsed
    // included, now that neither case deletes the row.
    // `23-103`: `status` replaces the old `is_active` boolean - projected, not filtered on, from the
    // identical two comparisons `Sql`'s own `WHERE` clause above uses, so a caller reading it trusts
    // the same live decision the production hot path makes, not a second one computed against a
    // different clock. `revoked_at is not null` is checked first and wins when a grant is both expired
    // and revoked - a revoke is the more specific, more recent fact (EnabledModuleDetailSummary's own
    // remarks state the reasoning). `id` and `revoked_at` are new selections, both needed to make a
    // revoke-then-re-grant's two rows for one module describable rather than merely present
    // (`adr/0155`). `order by enabled_at` makes that same multi-row case stable to render - Postgres
    // gives no ordering guarantee at all without one.
    private const string AllSql = """
        select id as "Id", module_key as "ModuleKey", trigger_words as "TriggerWords",
               entry_point as "EntryPoint", granted_by_owner as "GrantedByOwner",
               expires_at as "ExpiresAt", revoked_at as "RevokedAt",
               (case
                   when revoked_at is not null then 'Revoked'
                   when expires_at is not null and expires_at <= @Now then 'Expired'
                   else 'Active'
                end) as "Status"
        from enabled_modules
        where site_id = @SiteId
        order by enabled_at
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
        new EnabledModuleId(r.Id),
        new ModuleKey(r.ModuleKey),
        JsonSerializer.Deserialize<List<string>>(r.TriggerWords, TriggerWordsOptions)!,
        new Uri(r.EntryPoint, UriKind.Absolute),
        r.GrantedByOwner,
        r.ExpiresAt,
        r.RevokedAt,
        r.Status);

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
    /// hygiene <see cref="EnabledModuleDetailSummary"/>'s own remarks describe), plus <see cref="Id"/>,
    /// <see cref="RevokedAt"/> and <see cref="Status"/> (`23-103`, replacing the old <c>IsActive</c>
    /// boolean this row type carried before).</summary>
    private sealed class EnabledModuleDetailRow
    {
        public Guid Id { get; init; }

        public string ModuleKey { get; init; } = string.Empty;

        public string TriggerWords { get; init; } = string.Empty;

        public string EntryPoint { get; init; } = string.Empty;

        public bool GrantedByOwner { get; init; }

        public DateTimeOffset? ExpiresAt { get; init; }

        public DateTimeOffset? RevokedAt { get; init; }

        public string Status { get; init; } = string.Empty;
    }
}
