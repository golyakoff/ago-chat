using System.Text.RegularExpressions;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>One <c>messages</c> partition, as reported by Postgres's own catalog rather than computed
/// from a clock - `MessagePartitionPruneJob` must only ever act on a partition that genuinely exists,
/// never on a name it merely expects to.</summary>
public sealed record MessagePartitionInfo(string Name, DateOnly PeriodStart, DateOnly PeriodEnd);

/// <summary>
/// `15-04`: the read/drop half of partition pruning. Reads from <c>pg_inherits</c> - Postgres's own
/// record of which tables are partitions of <c>messages</c> - rather than reconstructing partition
/// names from a clock the way <see cref="PartitionMaintenanceJob"/> does for *creating* them; a job
/// that decides what to drop should look at what actually exists, not what it thinks should exist.
/// </summary>
public static partial class MessagePartitionPruneQuery
{
    // `messages_2026_01` - PartitionMaintenanceJob's own naming, `{yyyy_MM}`.
    [GeneratedRegex(@"^messages_(\d{4})_(\d{2})$")]
    private static partial Regex PartitionNamePattern();

    public static async Task<IReadOnlyList<MessagePartitionInfo>> ListPartitionsAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.relname
            FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            WHERE i.inhparent = 'messages'::regclass
            ORDER BY c.relname
            """;

        await using var command = new NpgsqlCommand(sql, connection);

        var partitions = new List<MessagePartitionInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            var match = PartitionNamePattern().Match(name);
            if (!match.Success)
            {
                // Not one of this job's monthly partitions (e.g. a future-shaped partition 13-06
                // introduces) - left alone rather than guessed at.
                continue;
            }

            var year = int.Parse(match.Groups[1].Value);
            var month = int.Parse(match.Groups[2].Value);
            var periodStart = new DateOnly(year, month, 1);
            partitions.Add(new MessagePartitionInfo(name, periodStart, periodStart.AddMonths(1)));
        }

        return partitions;
    }

    /// <summary>Idempotent by construction (<c>IF EXISTS</c>) - the same reason
    /// <see cref="PartitionMaintenanceJob"/>'s own <c>CREATE TABLE IF NOT EXISTS</c> is, for a second
    /// <c>Worker</c> replica racing this one on the same partition. <paramref name="partitionName"/>
    /// must already have matched <see cref="PartitionNamePattern"/> - callers only ever pass a name
    /// this class itself returned from <see cref="ListPartitionsAsync"/>, never a caller-supplied
    /// string, but the assert stays as the same defense-in-depth <see cref="PartitionMaintenanceJob"/>
    /// applies to its own interpolated identifiers.</summary>
    public static async Task DropPartitionAsync(
        NpgsqlConnection connection, string partitionName, CancellationToken cancellationToken)
    {
        if (!PartitionNamePattern().IsMatch(partitionName))
        {
            throw new ArgumentException($"'{partitionName}' is not a recognised messages partition name.", nameof(partitionName));
        }

        var sql = $"DROP TABLE IF EXISTS {partitionName};";
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
