using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `25-04`: the gate's own read - one row, two scalars, no change tracker (`adr/0004`'s read side).
/// Reached once per candidate conversation by <c>Ago.Chat.Worker.ConversationCategorizationJob</c> and
/// once per reply-draft request, which is why it is raw SQL over <see cref="NpgsqlDataSource"/> rather
/// than <see cref="AiAddOnEnablementRepository"/>'s EF query.
///
/// <para><b>The cut-off is derived here, not read as a column.</b> <c>enabled_at</c> keeps its value
/// through a disable (<see cref="AiAddOnEnablement"/>'s own remarks), so returning it unconditionally
/// would hand a caller a cut-off that is still true-looking for a site that has switched the add-on
/// off. The <c>CASE</c> below collapses the two columns into the one thing a caller may act on, which is
/// exactly what <see cref="AiAddOnEnablement.EffectiveFrom"/> does on the write side - the same rule
/// expressed twice because two access paths read it, and it is the rule that decides whether personal
/// data leaves the deployment.</para>
/// </summary>
public sealed class AiAddOnReadStore(NpgsqlDataSource dataSource) : IAiAddOnReadStore
{
    private const string Sql = """
        SELECT is_enabled,
               CASE WHEN is_enabled THEN enabled_at ELSE NULL END AS effective_from
        FROM ai_add_on_enablements
        WHERE site_id = @siteId
        """;

    public async Task<AiAddOnEnablementState?> GetForSiteAsync(SiteId siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(Sql, connection);
        command.Parameters.AddWithValue("siteId", siteId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var isEnabled = reader.GetBoolean(0);
        var effectiveFrom = reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1);
        return new AiAddOnEnablementState(isEnabled, effectiveFrom);
    }
}
