using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Module.Channels;
using Ago.Platform.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-01`: proves the resilience wrapping is actually wired, not merely described - the half a
/// reviewer would otherwise have to take on faith, since `14-01` ships no real provider to fail
/// against. No container, no fixture: the thing under test is a Polly pipeline and a decorator, and
/// the fake provider is an in-process delegate.
///
/// <para>This lives in <c>Ago.Chat.Integration.Tests</c> rather than <c>Ago.Chat.Application.Tests</c>
/// because <c>ChannelResiliencePipelines</c>/<c>ResilientInboundChannelAdapter</c> live in
/// <c>Ago.Chat.Module</c>, which Application's test project deliberately cannot see (the dependency
/// rule, enforced by <c>LayeringTests</c>). `6-05`'s own dispatcher tests sit here for the same
/// reason.</para>
/// </summary>
public class ResilientInboundChannelAdapterTests
{
    private static readonly OutboundChannelMessage Reply = new(
        ChannelKind.Sms,
        new ExternalChannelAddress("+70000000000"),
        new ConversationId(Guid.NewGuid()),
        new MessageId(Guid.NewGuid()),
        new MessageBody("an operator's answer"));

    private static ChannelResiliencePipelines Pipelines(Action<ResiliencePipelineOptions> configure)
    {
        var options = new ResiliencePipelineOptions();
        configure(options);
        return new ChannelResiliencePipelines(options);
    }

    [Fact]
    public void ForwardsTheInnerAdaptersKind_SoTheRegistryCanStillFindIt()
    {
        var inner = new StubAdapter(ChannelKind.Max);
        var wrapped = new ResilientInboundChannelAdapter(inner, Pipelines(_ => { }));

        Assert.Equal(ChannelKind.Max, wrapped.Kind);
    }

    [Fact]
    public async Task WhenTheProviderAnswers_TheOutcomeIsPassedThroughUnchanged()
    {
        var inner = new StubAdapter(ChannelKind.Sms) { Outcome = ChannelSendOutcome.Sent("provider-7") };
        var wrapped = new ResilientInboundChannelAdapter(inner, Pipelines(_ => { }));

        var outcome = await wrapped.SendAsync(Reply, CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.Equal("provider-7", outcome.ProviderMessageId);
        Assert.Equal(1, inner.Attempts);
    }

    /// <summary>
    /// A transient fault is thrown, so retry sees it and the call still succeeds - the contract
    /// <see cref="IInboundChannelAdapter.SendAsync"/> states, exercised end to end. Three failures
    /// then a success, with <c>MaxRetryAttempts = 3</c>, is deliberately the exact boundary: four
    /// attempts total.
    /// </summary>
    [Fact]
    public async Task ATransientFault_IsRetried()
    {
        var inner = new StubAdapter(ChannelKind.Sms) { FailuresBeforeSuccess = 3 };
        var wrapped = new ResilientInboundChannelAdapter(inner, Pipelines(options =>
            options.Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }));

        var outcome = await wrapped.SendAsync(Reply, CancellationToken.None);

        Assert.True(outcome.Delivered);
        Assert.Equal(4, inner.Attempts);
    }

    /// <summary>
    /// A terminal refusal is <em>not</em> retried, because it comes back as a return value rather
    /// than an exception. This is the distinction the port's contract turns on, and the one an
    /// adapter author is most likely to get wrong by catching a provider's 400 and rethrowing it.
    /// </summary>
    [Fact]
    public async Task ATerminalRefusal_IsNotRetried()
    {
        var inner = new StubAdapter(ChannelKind.Sms) { Outcome = ChannelSendOutcome.Refused("unknown recipient") };
        var wrapped = new ResilientInboundChannelAdapter(inner, Pipelines(options =>
            options.Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }));

        var outcome = await wrapped.SendAsync(Reply, CancellationToken.None);

        Assert.False(outcome.Delivered);
        Assert.Equal("unknown recipient", outcome.FailureReason);
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task AHangingProvider_IsCutOffByTheTimeout()
    {
        var inner = new StubAdapter(ChannelKind.Sms) { HangFor = TimeSpan.FromSeconds(30) };
        var wrapped = new ResilientInboundChannelAdapter(inner, Pipelines(options =>
            options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromMilliseconds(100) }));

        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => wrapped.SendAsync(Reply, CancellationToken.None));
    }

    /// <summary>
    /// The breaker opens on one channel and leaves the other alone - the reason
    /// <see cref="ChannelResiliencePipelines"/> keys per <see cref="ChannelKind"/> at all. An SMS
    /// aggregator's outage must not stop MAX replies from going out.
    /// </summary>
    [Fact]
    public async Task TheBreakerOpensPerChannel_NotGlobally()
    {
        var pipelines = Pipelines(options => options.CircuitBreaker = new ResilienceCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 2,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(30),
        });

        var brokenSms = new StubAdapter(ChannelKind.Sms) { FailuresBeforeSuccess = int.MaxValue };
        var wrappedSms = new ResilientInboundChannelAdapter(brokenSms, pipelines);

        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => wrappedSms.SendAsync(Reply, CancellationToken.None));
        }

        // The breaker is now open for SMS: the next call is rejected without the provider being asked.
        var attemptsBefore = brokenSms.Attempts;
        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => wrappedSms.SendAsync(Reply, CancellationToken.None));
        Assert.Equal(attemptsBefore, brokenSms.Attempts);

        // MAX, sharing the same thresholds but not the same pipeline instance, is untouched.
        var healthyMax = new StubAdapter(ChannelKind.Max);
        var wrappedMax = new ResilientInboundChannelAdapter(healthyMax, pipelines);
        var outcome = await wrappedMax.SendAsync(Reply with { Kind = ChannelKind.Max }, CancellationToken.None);

        Assert.True(outcome.Delivered);
    }

    /// <summary>
    /// The same instance is reused per channel - the property every stateful strategy above depends
    /// on, and the one a careless refactor to "build a pipeline per call" would silently destroy.
    /// </summary>
    [Fact]
    public void ThePipelineInstance_IsReusedPerChannel()
    {
        var pipelines = Pipelines(options =>
            options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromSeconds(1) });

        Assert.Same(pipelines.For(ChannelKind.Sms), pipelines.For(ChannelKind.Sms));
        Assert.NotSame(pipelines.For(ChannelKind.Sms), pipelines.For(ChannelKind.Max));
    }

    /// <summary>An adapter written with no awareness of resilience at all - which is the point of the
    /// decorator, and the shape `14-02`/`14-03` should be able to write theirs in.</summary>
    private sealed class StubAdapter(ChannelKind kind) : IInboundChannelAdapter
    {
        public ChannelKind Kind { get; } = kind;

        public int Attempts { get; private set; }

        public int FailuresBeforeSuccess { get; set; }

        public TimeSpan HangFor { get; set; }

        public ChannelSendOutcome Outcome { get; set; } = ChannelSendOutcome.Sent("stub");

        public async Task<ChannelSendOutcome> SendAsync(
            OutboundChannelMessage message, CancellationToken cancellationToken)
        {
            Attempts++;

            if (HangFor > TimeSpan.Zero)
            {
                await Task.Delay(HangFor, cancellationToken);
            }

            if (FailuresBeforeSuccess > 0)
            {
                if (FailuresBeforeSuccess != int.MaxValue)
                {
                    FailuresBeforeSuccess--;
                }

                throw new InvalidOperationException("stub transient provider fault");
            }

            return Outcome;
        }
    }
}
