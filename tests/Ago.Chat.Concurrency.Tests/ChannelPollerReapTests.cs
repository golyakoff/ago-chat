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
    }

    private sealed class PassthroughCipher : IChannelCredentialCipher
    {
        public byte[] Encrypt(string token) => throw new NotSupportedException("Not exercised by this test.");

        public string Decrypt(byte[] ciphertext) => throw new NotSupportedException("Not exercised by this test.");
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
