using Ago.Chat.Infrastructure.Postgres.Schema;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `8-08`: the wait-then-refuse loop, driven directly. No container and no database - the behaviour
/// worth pinning here is about the loop (does it re-check, does it give up, does it give up at the
/// right time), and driving that through a real migration would be slower and prove less, because a
/// real migration cannot be made to become current on the third poll on demand.
///
/// <para>This is why <see cref="SchemaVersionGuard.EnsureCurrentAsync"/> takes a delegate rather than a
/// <c>SchemaVersionCheck</c>. The cost is one level of indirection in production code; the purchase is
/// that the interesting states are reachable at all.</para>
/// </summary>
public class SchemaVersionGuardTests
{
    private static readonly SchemaGuardOptions Impatient = new()
    {
        WaitTimeout = TimeSpan.FromMilliseconds(300),
        PollInterval = TimeSpan.FromMilliseconds(20),
    };

    private static SchemaStatus Current() => new(["A", "B"], [], ["A", "B"]);

    private static SchemaStatus Behind() => new(["A"], ["B"], ["A", "B"]);

    [Fact]
    public async Task WhenTheSchemaIsAlreadyCurrent_ItReturnsWithoutWaiting()
    {
        var looks = 0;

        var status = await SchemaVersionGuard.EnsureCurrentAsync(
            _ => { looks++; return Task.FromResult(Current()); },
            Impatient, NullLogger.Instance, CancellationToken.None);

        Assert.True(status.IsCurrent);
        // Exactly one inspection: the happy path must cost one small SELECT and no delay at all, or
        // the guard becomes a reason not to add it to a host.
        Assert.Equal(1, looks);
    }

    /// <summary>
    /// The race the wait exists for: a host reaching its first check before the migrator Job has
    /// finished is not an error, it is a race with a known winner. Failing instantly would hand it to
    /// Kubernetes' restart backoff, which doubles to a five-minute cap.
    /// </summary>
    [Fact]
    public async Task WhenTheSchemaCatchesUpWhileWaiting_ItProceeds()
    {
        var looks = 0;

        var status = await SchemaVersionGuard.EnsureCurrentAsync(
            _ =>
            {
                looks++;
                return Task.FromResult(looks < 3 ? Behind() : Current());
            },
            Impatient, NullLogger.Instance, CancellationToken.None);

        Assert.True(status.IsCurrent);
        Assert.Equal(3, looks);
    }

    [Fact]
    public async Task WhenTheSchemaNeverCatchesUp_ItThrowsAndNamesThePendingMigrations()
    {
        var exception = await Assert.ThrowsAsync<SchemaOutOfDateException>(() =>
            SchemaVersionGuard.EnsureCurrentAsync(
                _ => Task.FromResult(Behind()), Impatient, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(["B"], exception.Status.Pending);
        // The message is the deliverable as much as the exception type is: the 2026-08-25 incident was
        // hard to diagnose exactly because nothing named the gap.
        Assert.Contains("B", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Ago.Chat.Migrator", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A zero timeout must still inspect once and refuse on what it found, rather than refusing
    /// without looking - the configuration a test or a fast-failing deployment would choose.
    /// </summary>
    [Fact]
    public async Task WithAZeroWaitTimeout_ItStillInspectsOnce()
    {
        var looks = 0;
        var options = new SchemaGuardOptions { WaitTimeout = TimeSpan.Zero, PollInterval = TimeSpan.Zero };

        await Assert.ThrowsAsync<SchemaOutOfDateException>(() =>
            SchemaVersionGuard.EnsureCurrentAsync(
                _ => { looks++; return Task.FromResult(Behind()); },
                options, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(1, looks);
    }

    /// <summary>
    /// `adr/0056`'s third open question, decided: a database ahead of this build is <b>not</b> a
    /// refusal. A pod rolled back to an older image against a newer schema is the expand/contract
    /// window working as designed, and refusing here would make rollback - the one recovery path this
    /// project actually has (`15-02`) - impossible.
    /// </summary>
    [Fact]
    public async Task WhenTheDatabaseIsAheadOfThisBuild_ItStartsAnyway()
    {
        var ahead = new SchemaStatus(["A", "B", "C"], [], ["A", "B"]);

        var status = await SchemaVersionGuard.EnsureCurrentAsync(
            _ => Task.FromResult(ahead), Impatient, NullLogger.Instance, CancellationToken.None);

        Assert.True(status.IsCurrent);
        Assert.Equal(["C"], status.AheadOfThisBuild);
    }
}
