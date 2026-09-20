using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `14-16`: proves, rather than merely asserting from the class's own XML doc comment ("the next
/// RefreshPollersAsync tick retries"), that a loop which loses its lease is actually reaped from
/// <c>_pollers</c> and its credential actually retried on the next refresh tick - not left parked
/// forever with a completed <see cref="Task"/> nobody looks at again. Needs no Postgres container:
/// <see cref="IChannelPollerOwnership"/> is faked to deny every acquire immediately, which is enough to
/// exercise <c>TelegramLongPollingService</c>'s own scheduling without ever reaching a real advisory
/// lock or a real Telegram call.
/// </summary>
public sealed class ChannelPollerReapTests
{
    [Fact]
    public async Task LeaseLostEveryTime_StillRetriesOnEveryRefreshTick_ProvingRefreshPollersAsyncReaps()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Telegram,
            tokenCiphertext: [], webhookSecretHash: [], now: DateTimeOffset.UtcNow);

        var owning = new AlwaysLosesTheLeaseOwnership();

        var services = new ServiceCollection();
        services.AddScoped<IChannelCredentialRepository>(_ => new FixedActiveCredentialRepository(credential));
        services.AddScoped<IChannelCredentialCipher>(_ => new PassthroughCipher());
        await using var provider = services.BuildServiceProvider();

        var client = new TelegramApiClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") });
        var apiOptions = Options.Create(new TelegramBotApiOptions());
        // The fastest tick this option type can express (int seconds) - see this test's own timing below.
        var pollingOptions = Options.Create(new TelegramLongPollingServiceOptions { CredentialRefreshIntervalSeconds = 1 });

        var service = new TelegramLongPollingService(
            client, provider.GetRequiredService<IServiceScopeFactory>(), owning, apiOptions, pollingOptions,
            NullLogger<TelegramLongPollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Long enough for several 1s refresh ticks - each one that finds _pollers still holding a
            // completed (lease-denied) Task for this credential and does NOT reap it would mean exactly
            // one acquire attempt total, however long this waits. Several attempts is the only way this
            // count grows past one.
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(
            owning.AcquireAttempts >= 3,
            $"Only {owning.AcquireAttempts} TryAcquireAsync call(s) in ~4s at a 1s refresh interval - " +
            "RefreshPollersAsync does not appear to reap a loop that lost its lease and retry it.");
    }

    /// <summary>
    /// Found live running `capacity-ramp` (`load/reports/2026-09-15-local-capacity-ramp.md`): a
    /// transient `PostgresException` from `RefreshPollersAsync`'s own `GetAllActiveAsync` call was
    /// unhandled, faulting `ExecuteAsync` and - via this host's unset (so default `StopHost`)
    /// `BackgroundServiceExceptionBehavior` - crashing the entire `Ago.Chat.Worker` process. Proven
    /// here at the level that actually matters: does `ExecuteAsync`'s own `Task` survive one failing
    /// tick and keep calling `GetAllActiveAsync` on the next one, or does it fault after the first
    /// failure and never call it again. Whether the surrounding generic `Host` then also survives is
    /// `BackgroundServiceExceptionBehavior`'s own concern, not this class's - this test targets the
    /// root cause `TelegramLongPollingService`/`MaxLongPollingService` can actually own.
    ///
    /// <para><b>Fails-before, actually run</b>: with `ExecuteAsync`'s own inner `try`/`catch` around
    /// `RefreshPollersAsync` reverted, `GetAllActiveAsync`'s call count stayed at 1 for the whole 4s
    /// window below (the assertion's own failure message shows exactly that) - and separately,
    /// `StopAsync` itself threw the simulated exception back out, since `BackgroundService.StopAsync`
    /// awaits `ExecuteTask` and rethrows whatever fault is sitting on it. Both are real, observable
    /// symptoms of the same missing catch; restored from the staged fix afterward.</para>
    /// </summary>
    [Fact]
    public async Task RefreshPollersAsyncThrowsOnce_DoesNotFaultExecuteAsync_KeepsRetryingOnTheNextTick()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Telegram,
            tokenCiphertext: [], webhookSecretHash: [], now: DateTimeOffset.UtcNow);

        var repository = new ThrowsOnFirstCallThenSucceedsRepository(credential);
        var owning = new AlwaysLosesTheLeaseOwnership();

        var services = new ServiceCollection();
        services.AddScoped<IChannelCredentialRepository>(_ => repository);
        services.AddScoped<IChannelCredentialCipher>(_ => new PassthroughCipher());
        await using var provider = services.BuildServiceProvider();

        var client = new TelegramApiClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") });
        var apiOptions = Options.Create(new TelegramBotApiOptions());
        var pollingOptions = Options.Create(new TelegramLongPollingServiceOptions { CredentialRefreshIntervalSeconds = 1 });

        var service = new TelegramLongPollingService(
            client, provider.GetRequiredService<IServiceScopeFactory>(), owning, apiOptions, pollingOptions,
            NullLogger<TelegramLongPollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        // Long enough for several 1s refresh ticks past the first, which always throws - a call count
        // stuck at 1 for this whole window means ExecuteAsync's own Task faulted on that first
        // exception and never ran the loop body again, the exact failure this test exists to catch.
        await Task.Delay(TimeSpan.FromSeconds(4));
        await service.StopAsync(CancellationToken.None);

        Assert.True(
            repository.CallCount >= 3,
            $"Only {repository.CallCount} GetAllActiveAsync call(s) in ~4s at a 1s refresh interval, " +
            "after the first call threw - ExecuteAsync's own loop does not appear to have survived a " +
            "transient RefreshPollersAsync failure and kept retrying on the next tick.");
    }

    /// <summary>Throws once (simulating the live `PostgresException` this test's own doc comment
    /// names), then behaves exactly like <see cref="FixedActiveCredentialRepository"/> from here on -
    /// isolating "did the loop survive one bad tick" from anything about the reap mechanism itself,
    /// which the other test in this file already covers.</summary>
    private sealed class ThrowsOnFirstCallThenSucceedsRepository(ChannelCredential credential) : IChannelCredentialRepository
    {
        private int _callCount;

        public int CallCount => _callCount;

        public Task<ChannelCredential?> GetActiveAsync(SiteId siteId, ChannelKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");

        public Task<IReadOnlyList<ChannelCredential>> GetAllActiveAsync(ChannelKind kind, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                throw new InvalidOperationException(
                    "Simulated transient failure - the same shape a wrapped PostgresException took live.");
            }

            return Task.FromResult<IReadOnlyList<ChannelCredential>>([credential]);
        }

        public Task<ChannelCredential?> GetByIdAsync(ChannelCredentialId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");

        public Task<ChannelCredential?> GetActiveByProviderAccountIdAsync(
            ChannelKind kind, string providerAccountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");

        public Task SaveAsync(ChannelCredential credential2, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");

        public Task ReloadAsync(ChannelCredential credential2, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");
    }

    private sealed class FixedActiveCredentialRepository(ChannelCredential credential) : IChannelCredentialRepository
    {
        public Task<ChannelCredential?> GetActiveAsync(SiteId siteId, ChannelKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");

        public Task<IReadOnlyList<ChannelCredential>> GetAllActiveAsync(ChannelKind kind, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ChannelCredential>>([credential]);

        public Task<ChannelCredential?> GetByIdAsync(ChannelCredentialId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");

        public Task<ChannelCredential?> GetActiveByProviderAccountIdAsync(
            ChannelKind kind, string providerAccountId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");

        public Task SaveAsync(ChannelCredential credential2, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");

        public Task ReloadAsync(ChannelCredential credential2, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by this test.");
    }

    private sealed class PassthroughCipher : IChannelCredentialCipher
    {
        public byte[] Encrypt(string token) => throw new NotSupportedException("Not exercised by this test.");

        public string Decrypt(byte[] ciphertext) => throw new NotSupportedException("Not exercised by this test.");
    }

    /// <summary>`25-170`: the entitlement watchdog's own pause is read at the very first gate
    /// <c>RefreshPollersAsync</c> applies (`.Where(c =&gt; c.EntitlementPausedAt is null)`), before a
    /// poller is ever started for that credential - so a paused credential should never even attempt to
    /// acquire the ownership lease, proven here by a lease double that would notice immediately if it
    /// were ever asked (<see cref="AlwaysLosesTheLeaseOwnership.AcquireAttempts"/> staying at zero for
    /// several refresh ticks, not merely "the credential row still shows `Active = true`").</summary>
    [Fact]
    public async Task ACredentialWithALapsedEntitlement_IsNeverPolled_AndNeverAttemptsToAcquireTheLease()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var credential = ChannelCredential.Register(
            new ChannelCredentialId(Guid.NewGuid()), siteId, ChannelKind.Telegram,
            tokenCiphertext: [], webhookSecretHash: [], now: DateTimeOffset.UtcNow);
        credential.PauseForLapsedEntitlement(DateTimeOffset.UtcNow);

        var owning = new AlwaysLosesTheLeaseOwnership();

        var services = new ServiceCollection();
        services.AddScoped<IChannelCredentialRepository>(_ => new FixedActiveCredentialRepository(credential));
        services.AddScoped<IChannelCredentialCipher>(_ => new PassthroughCipher());
        await using var provider = services.BuildServiceProvider();

        var client = new TelegramApiClient(new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") });
        var apiOptions = Options.Create(new TelegramBotApiOptions());
        var pollingOptions = Options.Create(new TelegramLongPollingServiceOptions { CredentialRefreshIntervalSeconds = 1 });

        var service = new TelegramLongPollingService(
            client, provider.GetRequiredService<IServiceScopeFactory>(), owning, apiOptions, pollingOptions,
            NullLogger<TelegramLongPollingService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // Several 1s refresh ticks - long enough that a bug re-including the paused credential in
            // the pollable set would have attempted to acquire its lease at least once by now.
            await Task.Delay(TimeSpan.FromSeconds(4));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(0, owning.AcquireAttempts);
    }

    /// <summary>Denies every acquire, immediately - PollOneCredentialAsync should therefore return right
    /// after this returns null, without ever touching client/scope/HTTP, giving _pollers a completed
    /// Task to (hopefully) reap on the very next tick.</summary>
    private sealed class AlwaysLosesTheLeaseOwnership : IChannelPollerOwnership
    {
        private int _acquireAttempts;

        public int AcquireAttempts => _acquireAttempts;

        public Task<IChannelPollerLease?> TryAcquireAsync(ChannelCredentialId credentialId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _acquireAttempts);
            return Task.FromResult<IChannelPollerLease?>(null);
        }
    }
}
