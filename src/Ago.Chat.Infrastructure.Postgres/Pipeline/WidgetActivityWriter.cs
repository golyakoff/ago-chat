using Ago.Chat.Domain;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres.Pipeline;

/// <summary>
/// `23-07`: the "batch write" half of the funnel's own accumulate-and-flush pipeline -
/// <c>Ago.Chat.Module.Pipeline.WidgetActivityFlusherService</c>'s own counterpart to
/// <see cref="MessageBatchWriter"/>, in this same namespace for the same "internal pipeline plumbing,
/// resolved by exactly the host that runs the flusher" reason that type's own registration
/// (`Ago.Chat.Api/Program.cs`) already establishes.
///
/// <para><b>One upsert per (site, day) delta, not a single multi-row statement.</b> A flush window is
/// small by construction (`WidgetActivityAccumulator`'s own remarks on why losing one is tolerable),
/// so the handful of distinct (site, day) pairs a real flush ever carries does not need Postgres's
/// multi-row `VALUES (...), (...)` syntax to stay cheap - one statement per delta keeps this method
/// readable against a raw <see cref="NpgsqlCommand"/> rather than building a parameter array by
/// hand.</para>
///
/// <para><b>`ON CONFLICT (site_id, day) DO UPDATE ... = table.column + excluded.column` is the whole
/// mechanism.</b> Each delta is an *increment*, never a replacement - two pods flushing the same
/// (site, day) pair between the same midnight rollovers must add their two counts together, not have
/// the second overwrite the first. This is exactly the property <see cref="SiteWidgetActivityEntity"/>'s
/// own composite primary key exists to make a single-statement upsert possible for.</para>
/// </summary>
public sealed class WidgetActivityWriter(NpgsqlDataSource dataSource)
{
    public async Task FlushAsync(
        IReadOnlyCollection<WidgetActivityDelta> deltas, CancellationToken cancellationToken)
    {
        if (deltas.Count == 0)
        {
            return;
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var delta in deltas)
        {
            await using var command = new NpgsqlCommand(
                """
                insert into site_widget_activity (site_id, day, loads, opens, conversations)
                values (@siteId, @day, @loads, @opens, @conversations)
                on conflict (site_id, day) do update set
                    loads = site_widget_activity.loads + excluded.loads,
                    opens = site_widget_activity.opens + excluded.opens,
                    conversations = site_widget_activity.conversations + excluded.conversations
                """,
                connection, transaction);
            command.Parameters.AddWithValue("siteId", delta.SiteId.Value);
            command.Parameters.AddWithValue("day", delta.Day);
            command.Parameters.AddWithValue("loads", delta.Loads);
            command.Parameters.AddWithValue("opens", delta.Opens);
            command.Parameters.AddWithValue("conversations", delta.Conversations);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>One (site, day) bucket's worth of increments, drained from
/// <c>WidgetActivityAccumulator</c> - plain data, carrying no reference to the in-memory counters it
/// was read from, so this type is safe to pass to a writer that has no business knowing a
/// <c>ConcurrentDictionary</c> exists.</summary>
public sealed record WidgetActivityDelta(SiteId SiteId, DateOnly Day, int Loads, int Opens, int Conversations);
