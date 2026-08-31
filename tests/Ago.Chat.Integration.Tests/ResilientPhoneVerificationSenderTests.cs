using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Module.PhoneVerification;
using Ago.Platform.Resilience;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `14-15`: proves the resilience wrapping is actually wired, not merely described - the same
/// "no container, no fixture, the thing under test is a Polly pipeline and a decorator" shape
/// <see cref="ResilientInboundChannelAdapterTests"/> already establishes, mirrored here rather than
/// <see cref="ResilientReplyDraftGeneratorTests"/>'s because <see cref="ResilientPhoneVerificationSender"/>
/// is the propagate-the-fault shape (that first class's own remarks), not the degrade-to-a-result one.
///
/// <para>Lives here, not <c>Ago.Chat.Application.Tests</c>, for the identical dependency-rule reason
/// those two files' own remarks state: <c>PhoneVerificationResiliencePipeline</c>/
/// <c>ResilientPhoneVerificationSender</c> live in <c>Ago.Chat.Module</c>, which Application's test
/// project cannot see.</para>
/// </summary>
public class ResilientPhoneVerificationSenderTests
{
    private static readonly PhoneVerificationDelivery Delivery = new("+79991234567", "482913", PhoneVerificationDeliveryMethod.Sms);

    private static PhoneVerificationResiliencePipeline Pipeline(Action<ResiliencePipelineOptions> configure)
    {
        var options = new ResiliencePipelineOptions();
        configure(options);
        return new PhoneVerificationResiliencePipeline(options);
    }

    [Fact]
    public async Task WhenTheGatewayAnswers_TheCallSucceeds()
    {
        var inner = new StubSender();
        var wrapped = new ResilientPhoneVerificationSender(inner, Pipeline(_ => { }));

        await wrapped.SendCodeAsync(Delivery, CancellationToken.None);

        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task ATransientFault_IsRetried_AndTheEventualSuccessCompletes()
    {
        var inner = new StubSender { FailuresBeforeSuccess = 3 };
        var wrapped = new ResilientPhoneVerificationSender(inner, Pipeline(options =>
            options.Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }));

        await wrapped.SendCodeAsync(Delivery, CancellationToken.None);

        Assert.Equal(4, inner.Attempts);
    }

    /// <summary>The Done-when this class exists to back: an exhausted-retry fault propagates rather than
    /// being swallowed - <see cref="ResilientPhoneVerificationSender"/>'s own remarks on why it matches
    /// <c>ResilientInboundChannelAdapter</c>'s shape, not <c>ResilientReplyDraftGenerator</c>'s, because
    /// its only caller (<c>PhoneVerificationDeliveryConsumer</c>) already has its own retry/DLQ
    /// backstop.</summary>
    [Fact]
    public async Task WhenEveryRetryIsExhausted_TheFaultPropagates_RatherThanBeingSwallowed()
    {
        var inner = new StubSender { FailuresBeforeSuccess = int.MaxValue };
        var wrapped = new ResilientPhoneVerificationSender(inner, Pipeline(options =>
            options.Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => wrapped.SendCodeAsync(Delivery, CancellationToken.None));
        Assert.Equal(3, inner.Attempts);
    }

    /// <summary>A terminal refusal (<see cref="PhoneVerificationSenderRefusedException"/> - the shape
    /// <see cref="UnconfiguredPhoneVerificationSender"/> always throws today) is not retried - exactly one
    /// attempt, then the exception propagates unchanged. The cost-avoidance reason
    /// <see cref="PhoneVerificationResiliencePipeline.IsRetryWorthy"/>'s own remarks give: this call is
    /// billed per attempt.</summary>
    [Fact]
    public async Task ATerminalRefusal_IsNotRetried_AndPropagates()
    {
        var inner = new StubSender { ThrowRefused = true };
        var wrapped = new ResilientPhoneVerificationSender(inner, Pipeline(options =>
            options.Retry = new ResilienceRetryOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Constant,
                Delay = TimeSpan.Zero,
            }));

        await Assert.ThrowsAsync<PhoneVerificationSenderRefusedException>(
            () => wrapped.SendCodeAsync(Delivery, CancellationToken.None));
        Assert.Equal(1, inner.Attempts);
    }

    [Fact]
    public async Task AHangingGateway_IsCutOffByTheTimeout()
    {
        var inner = new StubSender { HangFor = TimeSpan.FromSeconds(30) };
        var wrapped = new ResilientPhoneVerificationSender(inner, Pipeline(options =>
            options.Timeout = new ResilienceTimeoutOptions { Duration = TimeSpan.FromMilliseconds(100) }));

        await Assert.ThrowsAsync<TimeoutRejectedException>(
            () => wrapped.SendCodeAsync(Delivery, CancellationToken.None));
    }

    [Fact]
    public async Task WhenTheBreakerIsOpen_TheCallIsRejectedWithoutReachingTheGateway()
    {
        var pipeline = Pipeline(options => options.CircuitBreaker = new ResilienceCircuitBreakerOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 2,
            SamplingDuration = TimeSpan.FromSeconds(10),
            BreakDuration = TimeSpan.FromSeconds(30),
        });

        var inner = new StubSender { FailuresBeforeSuccess = int.MaxValue };
        var wrapped = new ResilientPhoneVerificationSender(inner, pipeline);

        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => wrapped.SendCodeAsync(Delivery, CancellationToken.None));
        }

        var attemptsBefore = inner.Attempts;
        await Assert.ThrowsAsync<BrokenCircuitException>(() => wrapped.SendCodeAsync(Delivery, CancellationToken.None));
        Assert.Equal(attemptsBefore, inner.Attempts);
    }

    /// <summary>An implementation written with no awareness of resilience at all - the same
    /// no-op-by-default shape <c>ResilientInboundChannelAdapterTests.StubAdapter</c>'s own remarks
    /// describe.</summary>
    private sealed class StubSender : IPhoneVerificationSender
    {
        public int Attempts { get; private set; }

        public int FailuresBeforeSuccess { get; set; }

        public bool ThrowRefused { get; set; }

        public TimeSpan HangFor { get; set; }

        public async Task SendCodeAsync(PhoneVerificationDelivery delivery, CancellationToken cancellationToken)
        {
            Attempts++;

            if (HangFor > TimeSpan.Zero)
            {
                await Task.Delay(HangFor, cancellationToken);
            }

            if (ThrowRefused)
            {
                throw new PhoneVerificationSenderRefusedException("stub terminal refusal");
            }

            if (FailuresBeforeSuccess > 0)
            {
                if (FailuresBeforeSuccess != int.MaxValue)
                {
                    FailuresBeforeSuccess--;
                }

                throw new InvalidOperationException("stub transient gateway fault");
            }
        }
    }
}

/// <summary>`14-15`: what `ChatModule` actually registers as <see cref="IPhoneVerificationSender"/> today
/// - see <see cref="UnconfiguredPhoneVerificationSender"/>'s own remarks for why this throws rather than
/// silently degrading.</summary>
public class UnconfiguredPhoneVerificationSenderTests
{
    [Fact]
    public async Task SendCodeAsync_AlwaysThrowsARefusedException_NeverSilentlySucceeds()
    {
        var sender = new UnconfiguredPhoneVerificationSender();

        await Assert.ThrowsAsync<PhoneVerificationSenderRefusedException>(
            () => sender.SendCodeAsync(
                new PhoneVerificationDelivery("+79991234567", "482913", PhoneVerificationDeliveryMethod.Sms),
                CancellationToken.None));
    }
}
