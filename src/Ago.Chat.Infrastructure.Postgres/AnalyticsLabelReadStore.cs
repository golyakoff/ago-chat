using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `26-237`/`adr/0186` §8.1: the operational-<c>ago_chat</c> side of the analytics label merge - the
/// concrete <see cref="IAnalyticsLabelReadStore"/>. It reads operator/tag display names from the
/// operational database (the source of truth for them), so the analytics report handlers can merge them
/// onto the id-keyed rollup rows in memory rather than issuing a forbidden cross-database join from
/// <c>ago_analytics</c> back here (design §3.2/§8.1).
///
/// <para><b>Batched, id-set lookups.</b> Each method takes the exact set of ids a single report produced
/// and resolves them in one round trip (<c>= any(@Ids)</c>), never one query per id. An empty set short-
/// circuits with no database call - the common case on the live read path, where the report store already
/// carried the names and nothing is left to resolve.</para>
///
/// <para><b>This adapter lives in <c>Infrastructure.Postgres</c>, not <c>Infrastructure.Analytics</c>, on
/// purpose:</b> it reads the operational <c>ago_chat</c> database through the operational
/// <see cref="NpgsqlDataSource"/> - the same store every other operator/tag read uses - whereas
/// <c>Infrastructure.Analytics</c> is defined as the adapter that opens the <em>separate</em>
/// <c>ago_analytics</c> connection. Keeping the label read here is what keeps that boundary honest.</para>
/// </summary>
public sealed class AnalyticsLabelReadStore(NpgsqlDataSource dataSource) : IAnalyticsLabelReadStore
{
    // display_name is nullable (an operator predating `23-02` has none); the WHERE drops those so the map
    // only ever contains real names - "present in the map" == "has a name", the contract the port states.
    private const string OperatorNamesSql = """
        select id as "Id", display_name as "DisplayName"
        from operators
        where site_id = @SiteId
          and id = any(@Ids)
          and display_name is not null
        """;

    private const string TagNamesSql = """
        select id as "Id", name as "Name"
        from tags
        where site_id = @SiteId
          and id = any(@Ids)
        """;

    public async Task<IReadOnlyDictionary<OperatorId, string>> GetOperatorDisplayNamesAsync(
        SiteId siteId, IReadOnlyCollection<OperatorId> operatorIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operatorIds);
        if (operatorIds.Count == 0)
        {
            return EmptyOperatorNames;
        }

        var ids = operatorIds.Select(o => o.Value).Distinct().ToArray();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<OperatorNameRow>(new CommandDefinition(
            OperatorNamesSql,
            new { SiteId = siteId.Value, Ids = ids },
            cancellationToken: cancellationToken));

        return rows.ToDictionary(r => new OperatorId(r.Id), r => r.DisplayName);
    }

    public async Task<IReadOnlyDictionary<TagId, string>> GetTagNamesAsync(
        SiteId siteId, IReadOnlyCollection<TagId> tagIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tagIds);
        if (tagIds.Count == 0)
        {
            return EmptyTagNames;
        }

        var ids = tagIds.Select(t => t.Value).Distinct().ToArray();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<TagNameRow>(new CommandDefinition(
            TagNamesSql,
            new { SiteId = siteId.Value, Ids = ids },
            cancellationToken: cancellationToken));

        return rows.ToDictionary(r => new TagId(r.Id), r => r.Name);
    }

    private static readonly IReadOnlyDictionary<OperatorId, string> EmptyOperatorNames =
        new Dictionary<OperatorId, string>();

    private static readonly IReadOnlyDictionary<TagId, string> EmptyTagNames =
        new Dictionary<TagId, string>();

    // The WHERE clause guarantees DisplayName is non-null on every row that comes back.
    private sealed record OperatorNameRow(Guid Id, string DisplayName);

    private sealed record TagNameRow(Guid Id, string Name);
}
