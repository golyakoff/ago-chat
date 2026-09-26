using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.RegisterOperatorDevice;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-82`: the race found live on the demo cluster - `26-06`'s Android client calls
/// `PUT /api/v1/me/devices/{installationId}` from three places (sign-in, `onNewToken`, a periodic
/// `WorkManager` job) that can reach the server for the identical `(operatorId, installationId)` pair
/// within milliseconds of each other, and `RegisterOperatorDeviceHandler`'s upsert was check-then-act,
/// so the loser's INSERT left as a raw `23505` 500 (9 occurrences in one 30-minute window).
///
/// <para><b>How the race is forced rather than hoped for.</b> Two genuinely concurrent
/// <c>HandleAsync</c> calls, each on its own <see cref="Infrastructure.Postgres.Persistence.AgoChatDbContext"/>
/// and its own real <see cref="OperatorDeviceRepository"/> - and
/// <see cref="RendezvousOperatorDeviceRepository"/> between them, which holds each caller at the exact
/// instant it has just been told "no such device yet" until *both* callers are standing there. Only then
/// is either released to insert. That is the check-then-act window held open deliberately, so the
/// collision is a certainty rather than a thread-scheduling accident - the same "inject the concurrent
/// write at a known point rather than hope two threads interleave" standard
/// <see cref="ConversationConcurrencyConflictTests"/>'s own <c>RacingConversationRepository</c> sets,
/// reached here with two real callers instead of one caller and an injected writer, because this item's
/// own Done-when asks for exactly that.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class RegisterOperatorDeviceConcurrencyTests(PostgresFixture fixture)
{
    [Fact]
    public async Task TwoConcurrentRegistrationsOfTheSameInstallation_BothSucceed_AndLeaveExactlyOneRow()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        const string installationId = "installation-racing";
        // One device, one token, registered twice at once - the real shape of the live bug (the same
        // phone's sign-in and its WorkManager job carry the identical token), not two different tokens.
        var token = $"token-{Guid.NewGuid():N}";
        var command = new RegisterOperatorDevice(
            operatorId, siteId, installationId, PushProvider.RuStore, "android", token);

        var rendezvous = new AsyncRendezvous(participants: 2);
        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var first = new RendezvousOperatorDeviceRepository(new OperatorDeviceRepository(firstDb), rendezvous);
        var second = new RendezvousOperatorDeviceRepository(new OperatorDeviceRepository(secondDb), rendezvous);

        var firstCall = Task.Run(() => new RegisterOperatorDeviceHandler(first, new UuidV7Generator(), new SystemClock())
            .HandleAsync(command, CancellationToken.None));
        var secondCall = Task.Run(() => new RegisterOperatorDeviceHandler(second, new UuidV7Generator(), new SystemClock())
            .HandleAsync(command, CancellationToken.None));

        var results = await Task.WhenAll(firstCall, secondCall);

        // The item's own Done-when: *both* succeed. Before this fix one of them threw
        // DbUpdateException("...ux_operator_devices_operator_installation") straight out to the API.
        Assert.All(results, result => Assert.True(result.IsSuccess));

        // Not a vacuous pass: exactly one of the two callers must actually have lost the insert and
        // recovered. A run where the rendezvous failed to hold the window open would still satisfy every
        // assertion below, so this is the one that proves the race happened at all.
        Assert.Equal(1, first.ConflictsObserved + second.ConflictsObserved);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.OperatorDevices
            .Where(d => d.OperatorId == operatorId && d.InstallationId == installationId)
            .ToListAsync(CancellationToken.None);
        var only = Assert.Single(rows);
        Assert.Equal(token, only.Token);
        Assert.Null(only.RevokedAt);
    }

    /// <summary>Two concurrent callers carrying *different* tokens - the `onNewToken` rotation racing
    /// the periodic job, which is the other real pairing of `26-06`'s three call sites. Both must still
    /// succeed against one row; which token wins is genuinely undefined (it is whichever call Postgres
    /// serialised last), exactly as it would be for two sequential calls, so this asserts only that the
    /// survivor is one of the two and never a merge of neither.</summary>
    [Fact]
    public async Task TwoConcurrentRegistrationsWithDifferentTokens_BothSucceed_AndLeaveExactlyOneRow()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        const string installationId = "installation-racing-rotation";
        var oldToken = $"token-old-{Guid.NewGuid():N}";
        var newToken = $"token-new-{Guid.NewGuid():N}";

        var rendezvous = new AsyncRendezvous(participants: 2);
        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var first = new RendezvousOperatorDeviceRepository(new OperatorDeviceRepository(firstDb), rendezvous);
        var second = new RendezvousOperatorDeviceRepository(new OperatorDeviceRepository(secondDb), rendezvous);

        var firstCall = Task.Run(() => new RegisterOperatorDeviceHandler(first, new UuidV7Generator(), new SystemClock())
            .HandleAsync(
                new RegisterOperatorDevice(operatorId, siteId, installationId, PushProvider.RuStore, "android", oldToken),
                CancellationToken.None));
        var secondCall = Task.Run(() => new RegisterOperatorDeviceHandler(second, new UuidV7Generator(), new SystemClock())
            .HandleAsync(
                new RegisterOperatorDevice(operatorId, siteId, installationId, PushProvider.RuStore, "android", newToken),
                CancellationToken.None));

        var results = await Task.WhenAll(firstCall, secondCall);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, first.ConflictsObserved + second.ConflictsObserved);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.OperatorDevices
            .Where(d => d.OperatorId == operatorId && d.InstallationId == installationId)
            .ToListAsync(CancellationToken.None);
        var only = Assert.Single(rows);
        Assert.True(
            only.Token == oldToken || only.Token == newToken,
            $"the surviving row must carry one of the two racing tokens, not '{only.Token}'");
        Assert.Null(only.RevokedAt);
    }

    /// <summary>
    /// `26-122`'s own new race, impossible before it: two concurrent first-ever registrations of the
    /// *same physical device*, but each carrying its own fresh `installationId` (two app processes
    /// racing a first launch, or a factory-reset device signing back in the instant a second install's
    /// call happens to land) - identical to the classic `26-82` race except the pair that matters is
    /// now `(operatorId, deviceId)`, not `(operatorId, installationId)`. Proves
    /// `ux_operator_devices_operator_device` (`OperatorDeviceConfiguration`'s own new index) closes the
    /// identical check-then-act window `ux_operator_devices_operator_installation` already closed for
    /// installation, and that `OperatorDeviceRepository.SaveAsync`'s two-constraint-name `when` clause
    /// actually catches this one.
    ///
    /// <para>Deliberately **different** tokens, unlike the classic installation race above: two distinct
    /// installs of the same device each hold their own token from their own SDK instance, and giving them
    /// the identical one would instead collide on `ux_operator_devices_provider_token_active` - a real
    /// constraint, but the restored-backup case that index exists for
    /// (`RegisterOperatorDeviceHandler`'s own step 1), not the race this test targets.</para>
    /// </summary>
    [Fact]
    public async Task TwoConcurrentRegistrationsOfTheSameDevice_WithDifferentInstallationIds_BothSucceed_AndLeaveExactlyOneRow()
    {
        var (siteId, operatorId) = await SeedSiteAndOperatorAsync();
        const string deviceId = "device-racing";
        var tokenA = $"token-a-{Guid.NewGuid():N}";
        var tokenB = $"token-b-{Guid.NewGuid():N}";

        var rendezvous = new AsyncRendezvous(participants: 2);
        await using var firstDb = fixture.CreateDbContext();
        await using var secondDb = fixture.CreateDbContext();
        var first = new RendezvousOperatorDeviceRepository(new OperatorDeviceRepository(firstDb), rendezvous);
        var second = new RendezvousOperatorDeviceRepository(new OperatorDeviceRepository(secondDb), rendezvous);

        var firstCall = Task.Run(() => new RegisterOperatorDeviceHandler(first, new UuidV7Generator(), new SystemClock())
            .HandleAsync(
                new RegisterOperatorDevice(operatorId, siteId, "installation-a", PushProvider.RuStore, "android", tokenA, deviceId),
                CancellationToken.None));
        var secondCall = Task.Run(() => new RegisterOperatorDeviceHandler(second, new UuidV7Generator(), new SystemClock())
            .HandleAsync(
                new RegisterOperatorDevice(operatorId, siteId, "installation-b", PushProvider.RuStore, "android", tokenB, deviceId),
                CancellationToken.None));

        var results = await Task.WhenAll(firstCall, secondCall);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(1, first.ConflictsObserved + second.ConflictsObserved);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.OperatorDevices
            .Where(d => d.OperatorId == operatorId && d.DeviceId == deviceId)
            .ToListAsync(CancellationToken.None);
        var only = Assert.Single(rows);
        Assert.True(
            only.Token == tokenA || only.Token == tokenB,
            $"the surviving row must carry one of the two racing tokens, not '{only.Token}'");
        Assert.Null(only.RevokedAt);
        Assert.True(
            only.InstallationId is "installation-a" or "installation-b",
            $"the surviving row must carry one of the two racing installation ids, not '{only.InstallationId}'");
    }

    private async Task<(SiteId SiteId, OperatorId OperatorId)> SeedSiteAndOperatorAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        await db.SaveChangesAsync(CancellationToken.None);
        return (siteId, operatorId);
    }

    /// <summary>
    /// The seam that makes the race deterministic. Every read and the save itself delegate to a real
    /// <see cref="OperatorDeviceRepository"/> against a real Postgres; the only thing added is a
    /// one-shot pause on the *first* <see cref="FindAsync"/> - the handler's own "does a row for this
    /// pair exist yet?" read - which blocks until every participant has completed that same read. Both
    /// callers therefore hold a `null` answer, taken before either wrote anything, which is precisely
    /// the state the live 23505s were produced from.
    ///
    /// <para>One-shot by counting calls on <em>this</em> instance: the handler's recovery path re-reads
    /// through <see cref="FindAsync"/> a second time, and gating that too would deadlock the loser
    /// against a winner that has already returned.</para>
    /// </summary>
    private sealed class RendezvousOperatorDeviceRepository(
        IOperatorDeviceRepository inner, AsyncRendezvous rendezvous) : IOperatorDeviceRepository
    {
        private int _findCalls;
        private int _conflictsObserved;

        /// <summary>How many times this caller's own insert lost the race and was handed
        /// <see cref="OperatorDeviceConcurrencyConflictException"/> - counted, rethrown untouched, and
        /// asserted on so a run where the rendezvous silently failed cannot pass vacuously.</summary>
        public int ConflictsObserved => _conflictsObserved;

        public async Task<OperatorDevice?> FindAsync(
            OperatorId operatorId, string installationId, CancellationToken cancellationToken)
        {
            var found = await inner.FindAsync(operatorId, installationId, cancellationToken);
            if (Interlocked.Increment(ref _findCalls) == 1)
            {
                await rendezvous.ArriveAndWaitAsync();
            }

            return found;
        }

        public async Task<OperatorDevice?> FindByDeviceAsync(
            OperatorId operatorId, string deviceId, CancellationToken cancellationToken)
        {
            var found = await inner.FindByDeviceAsync(operatorId, deviceId, cancellationToken);
            if (Interlocked.Increment(ref _findCalls) == 1)
            {
                await rendezvous.ArriveAndWaitAsync();
            }

            return found;
        }

        public Task<OperatorDevice?> FindActiveByTokenAsync(
            PushProvider provider, string token, CancellationToken cancellationToken) =>
            inner.FindActiveByTokenAsync(provider, token, cancellationToken);

        public Task<IReadOnlyList<OperatorDevice>> ListActiveForOperatorAsync(
            OperatorId operatorId, CancellationToken cancellationToken) =>
            inner.ListActiveForOperatorAsync(operatorId, cancellationToken);

        public async Task SaveAsync(OperatorDevice device, CancellationToken cancellationToken)
        {
            try
            {
                await inner.SaveAsync(device, cancellationToken);
            }
            catch (OperatorDeviceConcurrencyConflictException)
            {
                Interlocked.Increment(ref _conflictsObserved);
                throw;
            }
        }
    }

    /// <summary>An async barrier for a fixed number of participants - <see cref="System.Threading.Barrier"/>
    /// itself only blocks a thread, which inside an `async` test means occupying a thread-pool thread
    /// until its partner arrives. Guarded by a timeout so a future change that stops one participant
    /// from ever arriving fails the test instead of hanging the whole suite.</summary>
    private sealed class AsyncRendezvous(int participants)
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= participants)
            {
                _allArrived.TrySetResult();
            }

            return _allArrived.Task.WaitAsync(Timeout);
        }
    }
}
