using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Dapper;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>`25-148`: the Dapper adapter for <see cref="IPublicChannelLinkReadStore"/> -
/// <see cref="EnabledModuleReadStore"/>'s own "hand-written SQL over the write model, never through the
/// aggregate" shape (adr/0004), and the concrete proof of that port's own "cannot leak a token because it
/// never loads one" claim: this query selects <c>kind</c> and the two columns a handle can ever come from,
/// nothing else - no <c>token_ciphertext</c>, no <c>webhook_secret_hash</c>, no
/// <c>refresh_token_ciphertext</c> ever appears in this SQL text at all.</summary>
public sealed class PublicChannelLinkReadStore(NpgsqlDataSource dataSource) : IPublicChannelLinkReadStore
{
    // `25-147`'s own read-time-derivation decision, made concrete: VK's handle is never stored on
    // public_handle at all - it is computed here, every time, from provider_account_id (the community's
    // own numeric group id, already stored for VK by `14-08`). The `coalesce` tries the stored value
    // first for every other channel and only reaches the `case` for a row where it is null - which, for
    // every non-VK kind, means "no handle known yet" and correctly produces null (excluded by the `where`
    // below), while for VK it always produces a value the moment provider_account_id is set. Avito is
    // never reachable through either branch: its own public_handle is always null (`25-147`'s own "store
    // nothing" decision) and its kind never matches the `when` clause, so an Avito row is always excluded
    // regardless of what provider_account_id it (harmlessly) holds.
    private const string Sql = """
        select kind as "Kind",
               coalesce(public_handle, case when kind = 'Vk' then provider_account_id else null end) as "Handle"
        from channel_credentials
        where site_id = @SiteId
          and active
          and coalesce(public_handle, case when kind = 'Vk' then provider_account_id else null end) is not null
        """;

    public async Task<IReadOnlyList<PublicChannelLink>> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        var rows = await connection.QueryAsync<PublicChannelLinkRow>(new CommandDefinition(
            Sql, new { SiteId = siteId.Value }, cancellationToken: cancellationToken));

        return rows.Select(r => new PublicChannelLink(Enum.Parse<ChannelKind>(r.Kind), r.Handle)).ToList();
    }

    private sealed class PublicChannelLinkRow
    {
        public string Kind { get; init; } = string.Empty;

        public string Handle { get; init; } = string.Empty;
    }
}
