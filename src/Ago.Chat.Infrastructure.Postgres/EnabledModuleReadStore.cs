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

    private const string Sql = """
        select module_key as "ModuleKey", trigger_words as "TriggerWords", entry_point as "EntryPoint"
        from enabled_modules
        where site_id = @SiteId
        """;

    public async Task<IReadOnlyList<EnabledModuleSummary>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<EnabledModuleRow>(new CommandDefinition(
            Sql, new { SiteId = siteId.Value }, cancellationToken: cancellationToken));

        return rows.Select(ToSummary).ToList();
    }

    private static EnabledModuleSummary ToSummary(EnabledModuleRow r) => new(
        new ModuleKey(r.ModuleKey),
        JsonSerializer.Deserialize<List<string>>(r.TriggerWords, TriggerWordsOptions)!,
        new Uri(r.EntryPoint, UriKind.Absolute));

    private sealed class EnabledModuleRow
    {
        public string ModuleKey { get; init; } = string.Empty;

        public string TriggerWords { get; init; } = string.Empty;

        public string EntryPoint { get; init; } = string.Empty;
    }
}
