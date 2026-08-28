using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.FakeMax.Tests;
using Ago.Chat.Infrastructure.MaxBot;
using Ago.Chat.Module.Channels;
using Ago.Platform.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-02`'s own Done-when: "MAX's outbound API stopped/unreachable degrades gracefully (the circuit
/// breaker opens, the rest of the system's message pipeline is unaffected) - proven with a real
/// container-failure-style test, not asserted." <see cref="ResilientInboundChannelAdapterTests"/>
/// already proves the generic mechanism (`14-01`) against an in-process stub; this class proves the
/// same mechanism against MAX's real HTTP boundary - a real <see cref="MaxApiClient"/>, a real
/// <see cref="System.Net.Sockets.Socket"/>, and a real separate process
/// (<see cref="FakeMaxProcessFixture"/>) that this test can actually stop, the literal meaning of
/// "stopped/unreachable" rather than a simulated failure.
///
/// <para>Everything except the MAX boundary itself is a minimal stand-in: <see cref="MaxChannelAdapter"/>
/// needs <see cref="IConversationRepository"/>/<see cref="IChannelCredentialRepository"/>/
/// <see cref="IChannelCredentialCipher"/> only to resolve which site's bot to use, and this test fixes
/// that resolution to one always-active fake credential rather than standing up a real Postgres -
/// the thing under test is the resilience wrapping around the HTTP call, not the repository layer
/// (which <see cref="ChannelCredentialRepositoryTests"/> proves separately, against a real
/// Postgres).</para>
/// </summary>
public class MaxChannelAdapterResilienceTests
{
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly SiteId SiteId = new(Guid.NewGuid());

    private static readonly OutboundChannelMessage Reply = new(
        ChannelKind.Max, new ExternalChannelAddress("123456"), ConversationId, new MessageId(Guid.NewGuid()),
        new MessageBody("an operator's answer"));

    private static MaxChannelAdapter BuildAdapter(Uri baseAddress)
    {
        var services = new ServiceCollection();
        services.AddScoped<IConversationRepository>(_ => new FixedConversationRepository());
        services.AddScoped<IChannelCredentialRepository>(_ => new FixedChannelCredentialRepository());
        services.AddScoped<IChannelCredentialCipher>(_ => new PassthroughCipher());
        var provider = services.BuildServiceProvider();

        var httpClient = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(60) };
        var apiClient = new MaxApiClient(httpClient);

        return new MaxChannelAdapter(
            apiClient, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<MaxChannelAdapter>.Instance);
    }

    private static ChannelResiliencePipelines Pipelines(Action<ResiliencePipelineOptions> configure)
    {
        var options = new ResiliencePipelineOptions();
        configure(options);
        return new ChannelResiliencePipelines(options);
    }

    [Fact]
    public async Task WhenMaxAnswersNormally_TheMessageIsDelivered()
    {
        var fixture = new FakeMaxProcessFixture { DefaultBehavior = "ok" };
        await fixture.InitializeAsync();
        try
        {
            var adapter = BuildAdapter(fixture.BaseAddress);
            var wrapped = new ResilientInboundChannelAdapter(adapter, Pipelines(_ => { }));

            var outcome = await wrapped.SendAsync(Reply, CancellationToken.None);

            Assert.True(outcome.Delivered);
            Assert.Equal("fake-message-1", outcome.ProviderMessageId);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>A real hang, cut off by a real timeout - <see cref="TimeoutRejectedException"/> is
    /// Polly's own signal that the *pipeline's* timeout fired, not a bare
    /// <see cref="TaskCanceledException"/> the HttpClient might also throw on its own timeout, proving
    /// the pipeline is what is actually in control.</summary>
    [Fact]
    public async Task WhenMaxHangs_ThePipelineTimeoutCutsItOff()
    {
        var fixture = new FakeMaxProcessFixture { DefaultBehavior = "hang", HangSeconds = 30 };
        await fixture.InitializeAsync();
        try
        {
            var adapter = BuildAdapter(fixture.BaseAddress);
            var wrapped = new ResilientInboundChannelAdapter(adapter, Pipelines(options =>
                options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromSeconds(1) }));

            await Assert.ThrowsAsync<TimeoutRejectedException>(() => wrapped.SendAsync(Reply, CancellationToken.None));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>
    /// The Done-when, proven literally: MAX's own outbound API is stopped mid-test (the fake process is
    /// killed, so every call after this point hits a real connection refusal), repeated real HTTP
    /// failures open the breaker, and - the "rest of the system... unaffected" half - a different
    /// channel's own pipeline, sharing the same configured thresholds but not the same pipeline
    /// instance (`ChannelResiliencePipelines`' own per-<see cref="ChannelKind"/> keying, `14-01`),
    /// keeps working throughout.
    /// </summary>
    [Fact]
    public async Task WhenMaxIsStopped_TheBreakerOpens_AndAnotherChannelIsUnaffected()
    {
        var fixture = new FakeMaxProcessFixture { DefaultBehavior = "ok" };
        await fixture.InitializeAsync();

        var pipelines = Pipelines(options =>
        {
            // 500ms measured flaky on a shared GitHub Actions runner (ubuntu-latest, 2 vCPUs): a
            // connection refusal on loopback is normally sub-millisecond work, but thread-pool
            // scheduling delay under a loaded, noisy-neighbour runner can occasionally push even that
            // past a tight margin, and a timeout firing mid-call is indistinguishable from a real
            // provider fault to the code under test. 1500ms keeps 3x the original margin while still
            // leaving the 10s SamplingDuration below comfortable room for all five calls in this test,
            // even if every one of them were unlucky enough to hit the full margin (5 * 1.5s = 7.5s).
            options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromMilliseconds(1500) };
            options.CircuitBreaker = new ResilienceCircuitBreakerOptions
            {
                FailureRatio = 0.5,
                MinimumThroughput = 2,
                SamplingDuration = TimeSpan.FromSeconds(10),
                BreakDuration = TimeSpan.FromSeconds(30),
            };
        });

        var adapter = BuildAdapter(fixture.BaseAddress);
        var wrappedMax = new ResilientInboundChannelAdapter(adapter, pipelines);

        // Prove it works before the outage - otherwise "the breaker opened" would be indistinguishable
        // from "nothing was ever connected in the first place."
        var before = await wrappedMax.SendAsync(Reply, CancellationToken.None);
        Assert.True(before.Delivered);

        // MAX's own outbound API, stopped - the literal thing the backlog's own Done-when names.
        await fixture.StopAsync();

        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => wrappedMax.SendAsync(Reply, CancellationToken.None));
        }

        // The breaker is now open: the next call is rejected without a socket even being attempted.
        await Assert.ThrowsAsync<BrokenCircuitException>(() => wrappedMax.SendAsync(Reply, CancellationToken.None));

        // The rest of the system - a different channel, sharing the same thresholds but not the same
        // pipeline instance - is unaffected by MAX's own outage.
        var healthySms = new StubSmsAdapter();
        var wrappedSms = new ResilientInboundChannelAdapter(healthySms, pipelines);
        var smsOutcome = await wrappedSms.SendAsync(Reply with { Kind = ChannelKind.Sms }, CancellationToken.None);
        Assert.True(smsOutcome.Delivered);

        await fixture.DisposeAsync();
    }

    private sealed class FixedConversationRepository : IConversationRepository
    {
        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            Task.FromResult<Conversation?>(Conversation.Start(id, SiteId, new VisitorId(Guid.NewGuid()), DateTimeOffset.UtcNow));

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedChannelCredentialRepository : IChannelCredentialRepository
    {
        public Task<ChannelCredential?> GetActiveAsync(SiteId siteId, ChannelKind kind, CancellationToken cancellationToken) =>
            Task.FromResult<ChannelCredential?>(ChannelCredential.Register(
                new ChannelCredentialId(Guid.NewGuid()), siteId, kind, [1, 2, 3], [4, 5, 6], DateTimeOffset.UtcNow));

        public Task<ChannelCredential?> GetByIdAsync(ChannelCredentialId id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ChannelCredential>> GetAllActiveAsync(ChannelKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task SaveAsync(ChannelCredential credential, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PassthroughCipher : IChannelCredentialCipher
    {
        public byte[] Encrypt(string token) => System.Text.Encoding.UTF8.GetBytes(token);

        public string Decrypt(byte[] ciphertext) => "fake-token-not-a-real-secret";
    }

    private sealed class StubSmsAdapter : IInboundChannelAdapter
    {
        public ChannelKind Kind => ChannelKind.Sms;

        public Task<ChannelSendOutcome> SendAsync(OutboundChannelMessage message, CancellationToken cancellationToken) =>
            Task.FromResult(ChannelSendOutcome.Sent("stub-sms-1"));
    }
}
